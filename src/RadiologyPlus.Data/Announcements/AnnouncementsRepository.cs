using Npgsql;
using NpgsqlTypes;
using RadiologyPlus.Core.Announcements;
using RadiologyPlus.Core.Data;

namespace RadiologyPlus.Data.Announcements;

/// <summary>
/// Tenant-scoped repository for <c>core.status_banners</c>. Mirrors
/// <c>BillingRepository</c>: every method opens via <see cref="IAppDbContext.OpenAsync"/>
/// (which sets the <c>app.tenant_id</c> RLS GUC) and passes <c>tenant_id</c> explicitly
/// in every statement even though RLS also enforces it.
/// </summary>
public sealed class AnnouncementsRepository : IAnnouncementsRepository
{
    private readonly IAppDbContext _db;

    public AnnouncementsRepository(IAppDbContext db) => _db = db;

    private const string SelectColumns =
        "banner_id, tenant_id, message, severity, is_animated, marquee_speed, is_active, " +
        "starts_at, ends_at, facility_id, is_dismissible, " +
        "created_by_user_id, created_at, updated_at";

    public async Task<StatusBanner?> GetActiveBannerAsync(
        Guid tenantId, long? facilityId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {SelectColumns}
            FROM core.status_banners
            WHERE tenant_id = @t
              AND is_active = TRUE
              AND (facility_id IS NULL OR facility_id = @f)
              AND (starts_at IS NULL OR starts_at <= NOW())
              AND (ends_at   IS NULL OR ends_at   >  NOW())
            ORDER BY severity DESC, updated_at DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("f", (object?)facilityId ?? DBNull.Value);

        await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await rdr.ReadAsync(cancellationToken)) return null;
        return ReadBanner(rdr);
    }

    public async Task<IReadOnlyList<StatusBanner>> ListBannersAsync(
        Guid tenantId, int limit, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {SelectColumns}
            FROM core.status_banners
            WHERE tenant_id = @t
            ORDER BY created_at DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("limit", limit);

        var result = new List<StatusBanner>();
        await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await rdr.ReadAsync(cancellationToken))
            result.Add(ReadBanner(rdr));
        return result;
    }

    public async Task<StatusBanner> CreateBannerAsync(
        Guid tenantId, Guid createdByUserId, StatusBannerCreate input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var message = input.Message?.Trim() ?? "";
        if (message.Length == 0)
            throw new ArgumentException("Banner message must be non-empty.", nameof(input));
        if (input.Severity is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(input), input.Severity, "severity must be 1..4.");
        var speed = ClampSpeed(input.MarqueeSpeed);

        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        // Library model: at most one active banner per tenant. Creating an active banner
        // turns off whatever was active before (it stays in the collection).
        if (input.IsActive)
            await DeactivateAllAsync(conn, tx, tenantId, exceptBannerId: null, cancellationToken);

        StatusBanner row;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                INSERT INTO core.status_banners
                    (tenant_id, message, severity, is_animated, marquee_speed, is_active, starts_at, ends_at, created_by_user_id)
                VALUES (@t, @msg, @sev, @anim, @speed, @active, @starts, @ends, @user)
                RETURNING {SelectColumns}
                """;
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("msg", message);
            cmd.Parameters.AddWithValue("sev", input.Severity);
            cmd.Parameters.AddWithValue("anim", input.IsAnimated);
            cmd.Parameters.AddWithValue("speed", speed);
            cmd.Parameters.AddWithValue("active", input.IsActive);
            cmd.Parameters.Add(new NpgsqlParameter("starts", NpgsqlDbType.TimestampTz) { Value = (object?)input.StartsAt ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("ends", NpgsqlDbType.TimestampTz) { Value = (object?)input.EndsAt ?? DBNull.Value });
            cmd.Parameters.AddWithValue("user", createdByUserId);

            await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await rdr.ReadAsync(cancellationToken))
                throw new InvalidOperationException("status_banners insert returned no row.");
            row = ReadBanner(rdr);
        }

        await tx.CommitAsync(cancellationToken);
        return row;
    }

    public async Task<StatusBannerUpdateResult> UpdateBannerAsync(
        Guid tenantId, long bannerId, StatusBannerUpdate input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var message = input.Message?.Trim() ?? "";
        if (message.Length == 0)
            throw new ArgumentException("Banner message must be non-empty.", nameof(input));
        if (input.Severity is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(input), input.Severity, "severity must be 1..4.");
        var speed = ClampSpeed(input.MarqueeSpeed);

        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        // Read the existing row inside the transaction so the audit "before" matches what
        // we actually overwrote (mirrors UpdateCptCodeAsync).
        StatusBanner? before = null;
        await using (var selectCmd = conn.CreateCommand())
        {
            selectCmd.Transaction = tx;
            selectCmd.CommandText = $"""
                SELECT {SelectColumns}
                FROM core.status_banners
                WHERE tenant_id = @t AND banner_id = @id
                FOR UPDATE
                """;
            selectCmd.Parameters.AddWithValue("t", tenantId);
            selectCmd.Parameters.AddWithValue("id", bannerId);
            await using var reader = await selectCmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
                before = ReadBanner(reader);
        }
        if (before is null)
            throw new KeyNotFoundException($"No status banner {bannerId} for tenant {tenantId}.");

        await using (var updateCmd = conn.CreateCommand())
        {
            updateCmd.Transaction = tx;
            updateCmd.CommandText = """
                UPDATE core.status_banners SET
                    message       = @msg,
                    severity      = @sev,
                    is_animated   = @anim,
                    marquee_speed = @speed,
                    starts_at     = @starts,
                    ends_at       = @ends,
                    updated_at    = NOW()
                WHERE tenant_id = @t AND banner_id = @id
                """;
            updateCmd.Parameters.AddWithValue("t", tenantId);
            updateCmd.Parameters.AddWithValue("id", bannerId);
            updateCmd.Parameters.AddWithValue("msg", message);
            updateCmd.Parameters.AddWithValue("sev", input.Severity);
            updateCmd.Parameters.AddWithValue("anim", input.IsAnimated);
            updateCmd.Parameters.AddWithValue("speed", speed);
            updateCmd.Parameters.Add(new NpgsqlParameter("starts", NpgsqlDbType.TimestampTz) { Value = (object?)input.StartsAt ?? DBNull.Value });
            updateCmd.Parameters.Add(new NpgsqlParameter("ends", NpgsqlDbType.TimestampTz) { Value = (object?)input.EndsAt ?? DBNull.Value });
            await updateCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        StatusBanner after;
        await using (var afterCmd = conn.CreateCommand())
        {
            afterCmd.Transaction = tx;
            afterCmd.CommandText = $"""
                SELECT {SelectColumns}
                FROM core.status_banners
                WHERE tenant_id = @t AND banner_id = @id
                """;
            afterCmd.Parameters.AddWithValue("t", tenantId);
            afterCmd.Parameters.AddWithValue("id", bannerId);
            await using var reader = await afterCmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Banner row vanished mid-transaction.");
            after = ReadBanner(reader);
        }

        await tx.CommitAsync(cancellationToken);
        return new StatusBannerUpdateResult(before, after);
    }

    public async Task<StatusBanner> SetBannerActiveAsync(
        Guid tenantId, long bannerId, bool isActive, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        // Library model: activating one banner deactivates the others (it becomes the one
        // shown). Deactivating simply turns this one off. A 404 (no such row) rolls back.
        if (isActive)
            await DeactivateAllAsync(conn, tx, tenantId, exceptBannerId: bannerId, cancellationToken);

        StatusBanner row;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                UPDATE core.status_banners SET is_active = @active, updated_at = NOW()
                WHERE tenant_id = @t AND banner_id = @id
                RETURNING {SelectColumns}
                """;
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("id", bannerId);
            cmd.Parameters.AddWithValue("active", isActive);

            await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await rdr.ReadAsync(cancellationToken))
                throw new KeyNotFoundException($"No status banner {bannerId} for tenant {tenantId}.");
            row = ReadBanner(rdr);
        }

        await tx.CommitAsync(cancellationToken);
        return row;
    }

    // Library model helper: turn off every active banner for the tenant (optionally except
    // one), so activating/creating a visible banner leaves at most one active.
    private static async Task DeactivateAllAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, long? exceptBannerId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = exceptBannerId is null
            ? "UPDATE core.status_banners SET is_active = FALSE, updated_at = NOW() WHERE tenant_id = @t AND is_active = TRUE"
            : "UPDATE core.status_banners SET is_active = FALSE, updated_at = NOW() WHERE tenant_id = @t AND is_active = TRUE AND banner_id <> @id";
        cmd.Parameters.AddWithValue("t", tenantId);
        if (exceptBannerId is not null) cmd.Parameters.AddWithValue("id", exceptBannerId.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static short ClampSpeed(short speed) => speed < 1 ? (short)1 : speed > 10 ? (short)10 : speed;

    public async Task<bool> DeleteBannerAsync(
        Guid tenantId, long bannerId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM core.status_banners
            WHERE tenant_id = @t AND banner_id = @id
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("id", bannerId);
        var affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    private static StatusBanner ReadBanner(NpgsqlDataReader r) => new(
        BannerId:        r.GetInt64(0),
        TenantId:        r.GetGuid(1),
        Message:         r.GetString(2),
        Severity:        r.GetInt16(3),
        IsAnimated:      r.GetBoolean(4),
        MarqueeSpeed:    r.GetInt16(5),
        IsActive:        r.GetBoolean(6),
        StartsAt:        r.IsDBNull(7) ? null : new DateTimeOffset(r.GetDateTime(7), TimeSpan.Zero),
        EndsAt:          r.IsDBNull(8) ? null : new DateTimeOffset(r.GetDateTime(8), TimeSpan.Zero),
        FacilityId:      r.IsDBNull(9) ? null : r.GetInt64(9),
        IsDismissible:   r.GetBoolean(10),
        CreatedByUserId: r.IsDBNull(11) ? null : r.GetGuid(11),
        CreatedAt:       new DateTimeOffset(r.GetDateTime(12), TimeSpan.Zero),
        UpdatedAt:       new DateTimeOffset(r.GetDateTime(13), TimeSpan.Zero));
}
