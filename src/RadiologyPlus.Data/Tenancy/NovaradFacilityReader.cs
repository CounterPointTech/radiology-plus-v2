using Npgsql;
using RadiologyPlus.Core.Data;
using RadiologyPlus.Core.Tenancy;

namespace RadiologyPlus.Data.Tenancy;

/// <summary>
/// Reads the tenant's facility list from the live Novarad DB (shared.facilities)
/// for the Facilities page's import button. Same tenant-scoped pool as every
/// other Novarad read.
/// </summary>
public sealed class NovaradFacilityReader : INovaradFacilityReader
{
    private readonly INovaradDbContext _novarad;

    public NovaradFacilityReader(INovaradDbContext novarad) => _novarad = novarad;

    public async Task<IReadOnlyList<NovaradFacilityRow>> ListFacilitiesAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _novarad.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        // name is the short code-ish label, description the long one; both citext.
        cmd.CommandText = """
            SELECT facility_id,
                   COALESCE(NULLIF(TRIM(name::text), ''), 'facility-' || facility_id::text) AS code,
                   COALESCE(NULLIF(TRIM(description::text), ''), NULLIF(TRIM(name::text), ''), 'Facility #' || facility_id::text) AS display_name
            FROM shared.facilities
            ORDER BY facility_id
            """;

        var list = new List<NovaradFacilityRow>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new NovaradFacilityRow(
                FacilityId: reader.GetInt32(0),
                Code: reader.GetString(1),
                DisplayName: reader.GetString(2)));
        }
        return list;
    }
}
