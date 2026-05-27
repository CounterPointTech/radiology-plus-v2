using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RadiologyPlus.Core.Tenancy;
#pragma warning disable CA1416 // System.DirectoryServices.Protocols is Windows-targeted; Service host runs on Windows by design.
using System.DirectoryServices.Protocols;

namespace RadiologyPlus.NovaradAuth;

/// <summary>
/// LDAP branch of <see cref="NovaradCredentialValidator"/>. Used when a Novarad row has
/// <c>is_ldap_user = TRUE</c> or <c>use_ad_authentication = TRUE</c>. We do not link
/// against Novarad's AD code — we just bind to the configured AD server with the user's
/// supplied credentials and consider a successful bind as a valid password.
/// </summary>
public interface INovaradLdapAuthenticator
{
    Task<LdapAuthResult> AuthenticateAsync(
        TenantContext tenant,
        NovaradUserAuthRow user,
        string password,
        CancellationToken cancellationToken);
}

public sealed record LdapAuthResult(bool IsValid, string? FailureReason);

public sealed class NovaradLdapOptions
{
    /// <summary>Map of <c>tenant code → LDAP server</c>. Hostnames look like <c>"dc01.salient.local"</c> (port assumed 389/636 by UseLdaps).</summary>
    public IDictionary<string, NovaradLdapTenantConfig> Tenants { get; init; } = new Dictionary<string, NovaradLdapTenantConfig>(StringComparer.OrdinalIgnoreCase);
}

public sealed class NovaradLdapTenantConfig
{
    public string Server { get; init; } = "";
    public int Port { get; init; } = 389;
    public bool UseLdaps { get; init; }
    /// <summary>Default domain for users whose Novarad row has no <c>domain</c> column populated.</summary>
    public string? DefaultDomain { get; init; }
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

public sealed class NovaradLdapAuthenticator : INovaradLdapAuthenticator
{
    private readonly NovaradLdapOptions _options;
    private readonly ILogger<NovaradLdapAuthenticator> _logger;

    public NovaradLdapAuthenticator(
        IOptions<NovaradLdapOptions> options,
        ILogger<NovaradLdapAuthenticator> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task<LdapAuthResult> AuthenticateAsync(
        TenantContext tenant, NovaradUserAuthRow user, string password, CancellationToken cancellationToken)
    {
        if (!_options.Tenants.TryGetValue(tenant.TenantCode, out var cfg) || string.IsNullOrWhiteSpace(cfg.Server))
        {
            _logger.LogError(
                "LDAP authentication requested but no LDAP server is configured for tenant {Tenant}. " +
                "Configure NovaradLdap:Tenants:{Code} or remove is_ldap_user from this user.",
                tenant.TenantId, tenant.TenantCode);
            return Task.FromResult(new LdapAuthResult(false, "LDAP authentication is not configured for this site."));
        }

        var domain = !string.IsNullOrWhiteSpace(user.Domain) ? user.Domain : cfg.DefaultDomain;
        var bindUser = !string.IsNullOrWhiteSpace(domain) ? $"{domain}\\{user.UserName}" : user.UserName;

        try
        {
            var identifier = new LdapDirectoryIdentifier(cfg.Server, cfg.Port);
            using var connection = new LdapConnection(identifier)
            {
                AuthType = AuthType.Negotiate,
                Timeout = cfg.ConnectionTimeout,
            };
            connection.SessionOptions.ProtocolVersion = 3;
            if (cfg.UseLdaps)
            {
                connection.SessionOptions.SecureSocketLayer = true;
            }

            connection.Bind(new NetworkCredential(bindUser, password));
            return Task.FromResult(new LdapAuthResult(true, null));
        }
        catch (LdapException ex)
        {
            _logger.LogInformation(
                ex, "LDAP bind failed for tenant {Tenant} user {User} (code={Code}).",
                tenant.TenantCode, bindUser, ex.ErrorCode);
            return Task.FromResult(new LdapAuthResult(false, "Invalid username or password."));
        }
        catch (DirectoryOperationException ex)
        {
            _logger.LogWarning(ex, "LDAP directory error for tenant {Tenant} user {User}.", tenant.TenantCode, bindUser);
            return Task.FromResult(new LdapAuthResult(false, "Directory authentication failed."));
        }
    }
}
#pragma warning restore CA1416
