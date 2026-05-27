using System.Data;

namespace RadiologyPlus.Core.Data;

/// <summary>
/// Connection factory for the Radiology Plus application database (PostgreSQL).
/// Connections returned from this factory have <c>app.tenant_id</c> set so that
/// Row-Level Security policies scope every query to the current tenant.
/// </summary>
public interface IAppDbContext
{
    /// <summary>
    /// Open a connection scoped to the current tenant (via <see cref="Tenancy.ITenantContextAccessor"/>).
    /// </summary>
    Task<IDbConnection> OpenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Open a connection without any tenant scope. Use only for system-level operations
    /// (migrations, tenant provisioning, login flow before tenant is resolved).
    /// </summary>
    Task<IDbConnection> OpenUnscopedAsync(CancellationToken cancellationToken = default);
}
