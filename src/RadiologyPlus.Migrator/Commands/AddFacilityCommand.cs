using Npgsql;
using Serilog;

namespace RadiologyPlus.Migrator;

internal sealed class AddFacilityCommand
{
    private readonly string _connectionString;

    public AddFacilityCommand(string connectionString) => _connectionString = connectionString;

    public async Task<int> RunAsync(IReadOnlyDictionary<string, string> flags)
    {
        var tenantCode = flags.Required("tenant");
        var facilityCode = flags.Required("code");
        var name = flags.Required("name");
        var novaradFacilityId = flags.RequiredInt("novarad-facility-id");

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        Guid tenantId;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT tenant_id FROM tenancy.tenants WHERE code = @c";
            cmd.Parameters.AddWithValue("c", tenantCode);
            var result = await cmd.ExecuteScalarAsync();
            if (result is null or DBNull)
                throw new UsageException($"No tenant found with code '{tenantCode}'. Run 'init-tenant' first.");
            tenantId = (Guid)result;
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO tenancy.facilities (tenant_id, novarad_facility_id, code, display_name)
                VALUES (@t, @nfid, @c, @n)
                ON CONFLICT (tenant_id, novarad_facility_id) DO UPDATE SET
                    code = EXCLUDED.code,
                    display_name = EXCLUDED.display_name,
                    is_active = TRUE
                """;
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("nfid", novaradFacilityId);
            cmd.Parameters.AddWithValue("c", facilityCode);
            cmd.Parameters.AddWithValue("n", name);
            await cmd.ExecuteNonQueryAsync();
        }

        Log.Information(
            "Facility '{Code}' ({Name}, novarad_facility_id={NovaradId}) added to tenant '{Tenant}'.",
            facilityCode, name, novaradFacilityId, tenantCode);
        return 0;
    }
}
