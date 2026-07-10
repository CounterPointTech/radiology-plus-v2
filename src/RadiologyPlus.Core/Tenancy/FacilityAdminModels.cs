namespace RadiologyPlus.Core.Tenancy;

// ---------------------------------------------------------------------------
// Admin-facing records for the Facilities console page. tenancy.facilities maps
// the tenant's Novarad facilities (shared.facilities over the VPN) into
// Radiology Plus; identity.user_facilities hangs off it, and federated logins
// resolve their facility links through novarad_facility_id.
// ---------------------------------------------------------------------------

public sealed record FacilityAdminInfo(
    int FacilityId,
    int NovaradFacilityId,
    string Code,
    string DisplayName,
    bool IsActive,
    DateTimeOffset CreatedAt,
    int UserCount);

public sealed record FacilitySave(
    int NovaradFacilityId,
    string Code,
    string DisplayName,
    bool IsActive);

/// <summary>A row from the tenant's Novarad shared.facilities, shaped for import.</summary>
public sealed record NovaradFacilityRow(
    int FacilityId,
    string Code,
    string DisplayName);

public sealed record FacilityImportResult(
    int Inserted,
    int Updated,
    int Total);

/// <summary>
/// Management surface over tenancy.facilities. Tenant-scoped: every query passes
/// tenant_id explicitly (RLS enforces it too).
/// </summary>
public interface IFacilityAdminRepository
{
    Task<IReadOnlyList<FacilityAdminInfo>> ListAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<FacilityAdminInfo> CreateAsync(Guid tenantId, FacilitySave input, CancellationToken cancellationToken = default);

    /// <summary>Throws KeyNotFoundException when the facility does not exist.</summary>
    Task<FacilityAdminInfo> UpdateAsync(Guid tenantId, int facilityId, FacilitySave input, CancellationToken cancellationToken = default);

    /// <summary>Returns false when no row was removed. FK violation (mapped users) surfaces as PostgresException.</summary>
    Task<bool> DeleteAsync(Guid tenantId, int facilityId, CancellationToken cancellationToken = default);

    /// <summary>Upserts by (tenant_id, novarad_facility_id); existing rows keep their is_active.</summary>
    Task<FacilityImportResult> UpsertFromNovaradAsync(Guid tenantId, IReadOnlyList<NovaradFacilityRow> rows, CancellationToken cancellationToken = default);
}

/// <summary>Reads the facility list from the tenant's live Novarad DB (for the import button).</summary>
public interface INovaradFacilityReader
{
    Task<IReadOnlyList<NovaradFacilityRow>> ListFacilitiesAsync(CancellationToken cancellationToken = default);
}
