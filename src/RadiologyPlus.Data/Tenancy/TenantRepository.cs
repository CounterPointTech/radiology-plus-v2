using Npgsql;
using RadiologyPlus.Core.Data;
using RadiologyPlus.Core.Tenancy;

namespace RadiologyPlus.Data.Tenancy;

public sealed class TenantRepository : ITenantRepository
{
    private readonly IAppDbContext _db;

    public TenantRepository(IAppDbContext db) => _db = db;

    public async Task<Tenant?> GetByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT tenant_id, code, display_name, is_active, created_at FROM tenancy.tenants WHERE code = @c";
        cmd.Parameters.AddWithValue("c", code);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new Tenant(
            reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
            reader.GetBoolean(3),
            new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero));
    }

    public async Task<Tenant?> GetByIdAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT tenant_id, code, display_name, is_active, created_at FROM tenancy.tenants WHERE tenant_id = @id";
        cmd.Parameters.AddWithValue("id", tenantId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new Tenant(
            reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
            reader.GetBoolean(3),
            new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero));
    }

    public async Task<IReadOnlyList<Tenant>> ListActiveAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT tenant_id, code, display_name, is_active, created_at FROM tenancy.tenants WHERE is_active ORDER BY display_name";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var list = new List<Tenant>();
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new Tenant(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.GetBoolean(3),
                new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero)));
        }
        return list;
    }

    /// <summary>Resolve tenant by facility code (used during login when facility is the entry point).</summary>
    public async Task<Tenant?> GetByFacilityCodeAsync(string facilityCode, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT t.tenant_id, t.code, t.display_name, t.is_active, t.created_at
            FROM tenancy.tenants t
            JOIN tenancy.facilities f ON f.tenant_id = t.tenant_id
            WHERE f.code = @c AND t.is_active AND f.is_active
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("c", facilityCode);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new Tenant(
            reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
            reader.GetBoolean(3),
            new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero));
    }
}
