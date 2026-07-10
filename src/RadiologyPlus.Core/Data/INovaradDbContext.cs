using System.Data;

namespace RadiologyPlus.Core.Data;

/// <summary>
/// Connection factory for the current tenant's NovaPACS/NovaRIS PostgreSQL,
/// reached over the customer's site-to-site VPN tunnel. Connections are
/// authenticated as <c>radiology_plus_app</c> per tenant so that direct writes
/// are attributable to Radiology Plus in Novarad's own audit log.
/// </summary>
public interface INovaradDbContext
{
    /// <summary>Open a read connection to the current tenant's Novarad.</summary>
    Task<IDbConnection> OpenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Drop any cached data source for the tenant so the next open re-reads
    /// tenancy.novarad_connections. Call after the connection settings change —
    /// otherwise the pool keeps using the old credentials until restart.
    /// </summary>
    void InvalidateTenant(Guid tenantId);
}
