using Npgsql;
using NpgsqlTypes;
using RadiologyPlus.Core.Data;
using RadiologyPlus.Core.Identity;

namespace RadiologyPlus.Data.Identity;

/// <summary>
/// Management surface over identity.users / user_facilities / refresh_tokens for
/// the Users console page. Tenant-scoped: every query passes tenant_id explicitly.
/// Local users are fully editable; federated ones only through SetActiveAsync
/// (Novarad rewrites their profile at every login).
/// </summary>
public sealed class UserAdminRepository : IUserAdminRepository
{
    private readonly IAppDbContext _db;

    public UserAdminRepository(IAppDbContext db) => _db = db;

    private const string SummarySelect = """
        SELECT u.user_id, u.username, u.display_name, u.email, u.role, u.is_local, u.is_active,
               u.last_login_at, u.created_at,
               COALESCE(ARRAY_AGG(uf.facility_id) FILTER (WHERE uf.facility_id IS NOT NULL), ARRAY[]::int[]) AS facility_ids,
               (SELECT COUNT(*)::int FROM identity.refresh_tokens rt
                WHERE rt.user_id = u.user_id AND rt.revoked_at IS NULL AND rt.expires_at > NOW()) AS active_sessions
        FROM identity.users u
        LEFT JOIN identity.user_facilities uf ON uf.user_id = u.user_id
        """;

    public async Task<IReadOnlyList<UserAdminSummary>> ListAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            {SummarySelect}
            WHERE u.tenant_id = @t
            GROUP BY u.user_id
            ORDER BY u.username
            """;
        cmd.Parameters.AddWithValue("t", tenantId);

        var list = new List<UserAdminSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            list.Add(ReadSummary(reader));
        return list;
    }

    public async Task<UserAdminSummary?> GetAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        return await GetCoreAsync(conn, tenantId, userId, cancellationToken);
    }

    private static async Task<UserAdminSummary?> GetCoreAsync(
        NpgsqlConnection conn, Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            {SummarySelect}
            WHERE u.tenant_id = @t AND u.user_id = @id
            GROUP BY u.user_id
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("id", userId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSummary(reader) : null;
    }

    private static UserAdminSummary ReadSummary(NpgsqlDataReader reader) => new(
        UserId: reader.GetGuid(0),
        Username: reader.GetString(1),
        DisplayName: reader.GetString(2),
        Email: reader.IsDBNull(3) ? null : reader.GetString(3),
        Role: (Role)reader.GetInt16(4),
        IsLocal: reader.GetBoolean(5),
        IsActive: reader.GetBoolean(6),
        LastLoginAt: reader.IsDBNull(7) ? null : new DateTimeOffset(reader.GetDateTime(7), TimeSpan.Zero),
        CreatedAt: new DateTimeOffset(reader.GetDateTime(8), TimeSpan.Zero),
        FacilityIds: (int[])reader.GetValue(9),
        ActiveSessionCount: reader.GetInt32(10));

    // -- Create / update ---------------------------------------------------------

    public async Task<UserAdminSummary> CreateLocalAsync(Guid tenantId, LocalUserCreate input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        Guid userId;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO identity.users (tenant_id, username, display_name, email, role, is_local, password_hash, is_active)
                VALUES (@t, @u, @dn, @e, @r, TRUE, @pw, TRUE)
                RETURNING user_id
                """;
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("u", input.Username);
            cmd.Parameters.AddWithValue("dn", input.DisplayName);
            cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Text) { Value = (object?)input.Email ?? DBNull.Value });
            cmd.Parameters.AddWithValue("r", (short)input.Role);
            cmd.Parameters.AddWithValue("pw", input.PasswordHash);
            userId = (Guid)(await cmd.ExecuteScalarAsync(cancellationToken))!;
        }

        await ReplaceFacilityLinksAsync(conn, tenantId, userId, input.FacilityIds, cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return (await GetCoreAsync(conn, tenantId, userId, cancellationToken))!;
    }

    public async Task<UserAdminSummary> UpdateLocalAsync(Guid tenantId, Guid userId, LocalUserUpdate input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        await RequireLocalAsync(conn, tenantId, userId, cancellationToken);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE identity.users
                SET display_name = @dn, email = @e, role = @r, updated_at = NOW()
                WHERE tenant_id = @t AND user_id = @id
                """;
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("id", userId);
            cmd.Parameters.AddWithValue("dn", input.DisplayName);
            cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Text) { Value = (object?)input.Email ?? DBNull.Value });
            cmd.Parameters.AddWithValue("r", (short)input.Role);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await ReplaceFacilityLinksAsync(conn, tenantId, userId, input.FacilityIds, cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return (await GetCoreAsync(conn, tenantId, userId, cancellationToken))!;
    }

    public async Task<UserAdminSummary> SetActiveAsync(Guid tenantId, Guid userId, bool isActive, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE identity.users
            SET is_active = @active, updated_at = NOW()
            WHERE tenant_id = @t AND user_id = @id
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("id", userId);
        cmd.Parameters.AddWithValue("active", isActive);
        if (await cmd.ExecuteNonQueryAsync(cancellationToken) == 0)
            throw new KeyNotFoundException($"User {userId} not found for tenant {tenantId}.");

        return (await GetCoreAsync(conn, tenantId, userId, cancellationToken))!;
    }

    public async Task SetPasswordAsync(Guid tenantId, Guid userId, string passwordHash, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await RequireLocalAsync(conn, tenantId, userId, cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE identity.users
            SET password_hash = @pw, updated_at = NOW()
            WHERE tenant_id = @t AND user_id = @id AND is_local
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("id", userId);
        cmd.Parameters.AddWithValue("pw", passwordHash);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RequireLocalAsync(
        NpgsqlConnection conn, Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT is_local FROM identity.users WHERE tenant_id = @t AND user_id = @id";
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("id", userId);
        var isLocal = await cmd.ExecuteScalarAsync(cancellationToken);
        if (isLocal is null)
            throw new KeyNotFoundException($"User {userId} not found for tenant {tenantId}.");
        if (!(bool)isLocal)
            throw new InvalidOperationException("This is a federated Novarad account — its profile is managed by Novarad and would be overwritten at the next sign-in.");
    }

    private static async Task ReplaceFacilityLinksAsync(
        NpgsqlConnection conn, Guid tenantId, Guid userId, IReadOnlyList<int> facilityIds, CancellationToken cancellationToken)
    {
        await using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM identity.user_facilities WHERE user_id = @u";
            del.Parameters.AddWithValue("u", userId);
            await del.ExecuteNonQueryAsync(cancellationToken);
        }

        if (facilityIds.Count == 0) return;

        await using var ins = conn.CreateCommand();
        // Join through tenancy.facilities so a facility id from another tenant can't be linked.
        ins.CommandText = """
            INSERT INTO identity.user_facilities (user_id, facility_id)
            SELECT @u, facility_id FROM tenancy.facilities
            WHERE tenant_id = @t AND facility_id = ANY(@ids)
            ON CONFLICT DO NOTHING
            """;
        ins.Parameters.AddWithValue("u", userId);
        ins.Parameters.AddWithValue("t", tenantId);
        ins.Parameters.AddWithValue("ids", facilityIds.Distinct().ToArray());
        await ins.ExecuteNonQueryAsync(cancellationToken);
    }

    // -- Sessions -------------------------------------------------------------------

    public async Task<IReadOnlyList<UserSessionInfo>> ListActiveSessionsAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT token_id, created_at, expires_at
            FROM identity.refresh_tokens
            WHERE tenant_id = @t AND user_id = @id AND revoked_at IS NULL AND expires_at > NOW()
            ORDER BY created_at DESC
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("id", userId);

        var list = new List<UserSessionInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new UserSessionInfo(
                TokenId: reader.GetGuid(0),
                CreatedAt: new DateTimeOffset(reader.GetDateTime(1), TimeSpan.Zero),
                ExpiresAt: new DateTimeOffset(reader.GetDateTime(2), TimeSpan.Zero)));
        }
        return list;
    }

    public async Task<int> RevokeAllSessionsAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE identity.refresh_tokens
            SET revoked_at = NOW()
            WHERE tenant_id = @t AND user_id = @id AND revoked_at IS NULL AND expires_at > NOW()
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("id", userId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
