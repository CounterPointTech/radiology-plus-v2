namespace RadiologyPlus.Core.Billing;

/// <summary>
/// Reads and writes a tenant's M*Modal ClinicalDataStore connection
/// (<c>tenancy.mmodal_connections</c>) — the runtime equivalent of the
/// <c>set-mmodal-connection</c> migrator command, backing the in-app connection settings.
/// The password is AES-GCM encrypted at rest; it is never returned.
/// </summary>
public interface IMModalConnectionStore
{
    /// <summary>The tenant's connection config (without the password), or null if unset.</summary>
    Task<MModalConnectionInfo?> GetAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Create or update the connection. When <see cref="MModalConnectionUpsert.Password"/> is
    /// null the existing password is kept (so an edit needn't re-enter it); on first setup a
    /// password is required (enforced by the caller).
    /// </summary>
    Task UpsertAsync(Guid tenantId, MModalConnectionUpsert upsert, CancellationToken cancellationToken = default);

    /// <summary>Remove the connection (disables the write-back). False if none existed.</summary>
    Task<bool> DeleteAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

/// <summary>A tenant's M*Modal connection config, password redacted to a <see cref="HasPassword"/> flag.</summary>
public sealed record MModalConnectionInfo(
    string Host,
    int Port,
    string Database,
    string Username,
    bool UseSsl,
    bool TrustServerCert,
    Guid? IssuerKey,
    bool HasPassword,
    DateTimeOffset UpdatedAt);

/// <summary>Write-shape for the connection settings form. <see cref="Password"/> null = keep existing.</summary>
public sealed record MModalConnectionUpsert(
    string Host,
    int Port,
    string Database,
    string Username,
    string? Password,
    bool UseSsl,
    bool TrustServerCert,
    Guid? IssuerKey);
