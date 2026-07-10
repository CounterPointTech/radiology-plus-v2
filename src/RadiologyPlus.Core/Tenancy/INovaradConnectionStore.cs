namespace RadiologyPlus.Core.Tenancy;

/// <summary>
/// Reads and writes a tenant's Novarad connection (<c>tenancy.novarad_connections</c>)
/// — the runtime equivalent of the seeding done at tenant setup, backing the console's
/// Settings page. The password is AES-GCM encrypted at rest; it is never returned.
/// Mirrors IMModalConnectionStore.
/// </summary>
public interface INovaradConnectionStore
{
    /// <summary>The tenant's connection config (without the password), or null if unset.</summary>
    Task<NovaradConnectionInfo?> GetAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Create or update the connection. When <see cref="NovaradConnectionUpsert.Password"/> is
    /// null the existing password is kept (so an edit needn't re-enter it); on first setup a
    /// password is required (enforced by the caller).
    /// </summary>
    Task UpsertAsync(Guid tenantId, NovaradConnectionUpsert upsert, CancellationToken cancellationToken = default);

    /// <summary>Remove the connection (kills federated login + all Novarad reads). False if none existed.</summary>
    Task<bool> DeleteAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

/// <summary>A tenant's Novarad connection config, password redacted to a <see cref="HasPassword"/> flag.</summary>
public sealed record NovaradConnectionInfo(
    string Host,
    int Port,
    string Database,
    string Username,
    bool UseSsl,
    string NovaradAuditTable,
    string? Notes,
    bool HasPassword,
    DateTimeOffset UpdatedAt);

/// <summary>Write-shape for the settings form. <see cref="Password"/> null = keep existing.</summary>
public sealed record NovaradConnectionUpsert(
    string Host,
    int Port,
    string Database,
    string Username,
    string? Password,
    bool UseSsl,
    string NovaradAuditTable,
    string? Notes);
