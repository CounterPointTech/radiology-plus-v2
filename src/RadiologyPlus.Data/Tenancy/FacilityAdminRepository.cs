using Npgsql;
using RadiologyPlus.Core.Data;
using RadiologyPlus.Core.Tenancy;

namespace RadiologyPlus.Data.Tenancy;

/// <summary>
/// Management surface over tenancy.facilities for the Facilities console page.
/// Tenant-scoped: every query passes tenant_id explicitly.
/// </summary>
public sealed class FacilityAdminRepository : IFacilityAdminRepository
{
    private readonly IAppDbContext _db;

    public FacilityAdminRepository(IAppDbContext db) => _db = db;

    private const string InfoSelect = """
        SELECT f.facility_id, f.novarad_facility_id, f.code, f.display_name, f.is_active, f.created_at,
               (SELECT COUNT(*)::int FROM identity.user_facilities uf WHERE uf.facility_id = f.facility_id) AS user_count
        FROM tenancy.facilities f
        """;

    public async Task<IReadOnlyList<FacilityAdminInfo>> ListAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{InfoSelect} WHERE f.tenant_id = @t ORDER BY f.code";
        cmd.Parameters.AddWithValue("t", tenantId);

        var list = new List<FacilityAdminInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            list.Add(ReadInfo(reader));
        return list;
    }

    public async Task<FacilityAdminInfo> CreateAsync(Guid tenantId, FacilitySave input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenancy.facilities (tenant_id, novarad_facility_id, code, display_name, is_active)
            VALUES (@t, @nfid, @c, @dn, @active)
            RETURNING facility_id
            """;
        AddSaveParams(cmd, tenantId, input);
        var facilityId = (int)(await cmd.ExecuteScalarAsync(cancellationToken))!;
        return (await GetCoreAsync(conn, tenantId, facilityId, cancellationToken))!;
    }

    public async Task<FacilityAdminInfo> UpdateAsync(Guid tenantId, int facilityId, FacilitySave input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE tenancy.facilities
            SET novarad_facility_id = @nfid, code = @c, display_name = @dn, is_active = @active
            WHERE tenant_id = @t AND facility_id = @id
            """;
        AddSaveParams(cmd, tenantId, input);
        cmd.Parameters.AddWithValue("id", facilityId);
        if (await cmd.ExecuteNonQueryAsync(cancellationToken) == 0)
            throw new KeyNotFoundException($"Facility {facilityId} not found for tenant {tenantId}.");
        return (await GetCoreAsync(conn, tenantId, facilityId, cancellationToken))!;
    }

    public async Task<bool> DeleteAsync(Guid tenantId, int facilityId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM tenancy.facilities WHERE tenant_id = @t AND facility_id = @id";
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("id", facilityId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<FacilityImportResult> UpsertFromNovaradAsync(
        Guid tenantId, IReadOnlyList<NovaradFacilityRow> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        var inserted = 0;
        var updated = 0;
        foreach (var row in rows)
        {
            await using var cmd = conn.CreateCommand();
            // Existing rows keep their is_active — an import must not resurrect a
            // facility an admin deliberately switched off.
            cmd.CommandText = """
                INSERT INTO tenancy.facilities (tenant_id, novarad_facility_id, code, display_name, is_active)
                VALUES (@t, @nfid, @c, @dn, TRUE)
                ON CONFLICT (tenant_id, novarad_facility_id) DO UPDATE SET
                    code = EXCLUDED.code,
                    display_name = EXCLUDED.display_name
                RETURNING (xmax = 0) AS was_insert
                """;
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("nfid", row.FacilityId);
            cmd.Parameters.AddWithValue("c", row.Code);
            cmd.Parameters.AddWithValue("dn", row.DisplayName);
            var wasInsert = (bool)(await cmd.ExecuteScalarAsync(cancellationToken))!;
            if (wasInsert) inserted++;
            else updated++;
        }

        await tx.CommitAsync(cancellationToken);
        return new FacilityImportResult(inserted, updated, rows.Count);
    }

    private static async Task<FacilityAdminInfo?> GetCoreAsync(
        NpgsqlConnection conn, Guid tenantId, int facilityId, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{InfoSelect} WHERE f.tenant_id = @t AND f.facility_id = @id";
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("id", facilityId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadInfo(reader) : null;
    }

    private static void AddSaveParams(NpgsqlCommand cmd, Guid tenantId, FacilitySave input)
    {
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("nfid", input.NovaradFacilityId);
        cmd.Parameters.AddWithValue("c", input.Code);
        cmd.Parameters.AddWithValue("dn", input.DisplayName);
        cmd.Parameters.AddWithValue("active", input.IsActive);
    }

    private static FacilityAdminInfo ReadInfo(NpgsqlDataReader reader) => new(
        FacilityId: reader.GetInt32(0),
        NovaradFacilityId: reader.GetInt32(1),
        Code: reader.GetString(2),
        DisplayName: reader.GetString(3),
        IsActive: reader.GetBoolean(4),
        CreatedAt: new DateTimeOffset(reader.GetDateTime(5), TimeSpan.Zero),
        UserCount: reader.GetInt32(6));
}
