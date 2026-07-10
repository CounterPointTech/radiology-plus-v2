namespace RadiologyPlus.Notifications.Channels;

/// <summary>
/// Reads and writes a tenant's Microsoft Graph email credentials
/// (<c>tenancy.graph_email_connections</c>), backing the notifications
/// email-settings page. The client secret is AES-GCM encrypted at rest; it is
/// never returned to callers — only <see cref="ResolveAsync"/> decrypts it, for
/// the channel at send time.
/// </summary>
public interface IGraphEmailSettingsStore
{
    /// <summary>The tenant's Graph config (secret redacted), or null if unset.</summary>
    Task<GraphEmailSettingsInfo?> GetAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Create or update the settings. When <see cref="GraphEmailSettingsUpsert.ClientSecret"/>
    /// is null the existing secret is kept (so an edit needn't re-enter it); on first setup a
    /// secret is required (enforced by the caller).
    /// </summary>
    Task UpsertAsync(Guid tenantId, GraphEmailSettingsUpsert upsert, CancellationToken cancellationToken = default);

    /// <summary>Remove the settings (email channel falls back to appsettings). False if none existed.</summary>
    Task<bool> DeleteAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Decrypted, send-ready options for the channel. Null when the tenant has no row —
    /// the channel then falls back to the appsettings-configured options.
    /// </summary>
    Task<GraphEmailOptions?> ResolveAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

/// <summary>A tenant's Graph email config, secret redacted to a <see cref="HasClientSecret"/> flag.</summary>
public sealed record GraphEmailSettingsInfo(
    string GraphTenantId,
    string ClientId,
    bool HasClientSecret,
    string FromAddress,
    DateTimeOffset UpdatedAt);

/// <summary>Write-shape for the settings form. <see cref="ClientSecret"/> null = keep existing.</summary>
public sealed record GraphEmailSettingsUpsert(
    string GraphTenantId,
    string ClientId,
    string? ClientSecret,
    string FromAddress);
