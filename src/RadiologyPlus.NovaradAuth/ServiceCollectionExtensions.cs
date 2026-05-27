using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RadiologyPlus.Core.Identity;

namespace RadiologyPlus.NovaradAuth;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the pure-.NET Novarad federated credential validator.
    /// Expects <c>INovaradDbContext</c>, <c>ITenantContextAccessor</c>, and <c>ITenantRepository</c>
    /// to already be registered (they live in RadiologyPlus.Data / Core).
    /// </summary>
    /// <param name="configuration">Pass <c>builder.Configuration</c>; reads <c>NovaradLdap</c> section.</param>
    public static IServiceCollection AddNovaradFederatedAuth(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddMemoryCache();
        services.Configure<NovaradLdapOptions>(configuration.GetSection("NovaradLdap"));
        services.AddSingleton<INovaradPasswordHasher, NovaradPasswordHasher>();
        services.AddScoped<INovaradLdapAuthenticator, NovaradLdapAuthenticator>();
        services.AddScoped<INovaradCredentialValidator, NovaradCredentialValidator>();
        return services;
    }
}
