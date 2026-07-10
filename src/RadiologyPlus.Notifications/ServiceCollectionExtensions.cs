using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RadiologyPlus.Notifications.Channels;

namespace RadiologyPlus.Notifications;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRadiologyPlusNotifications(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<INotificationService, NotificationService>();
        services.Configure<GraphEmailOptions>(configuration.GetSection("Notifications:GraphEmail"));

        // Register channels. Override these in tests or when a tenant uses a different provider.
        // GraphEmailChannel is also exposed concretely so the settings endpoint can run
        // a credentials test through the same send path.
        services.AddSingleton<GraphEmailChannel>();
        services.AddSingleton<INotificationChannelSender>(sp => sp.GetRequiredService<GraphEmailChannel>());
        // Teams + SMS deferred to Phase 4 — add LogOnly as a permanent dev fallback if Graph not configured.
        services.AddHttpClient();

        return services;
    }

    public static IServiceCollection AddNotificationOrchestratorHostedService(this IServiceCollection services)
    {
        services.AddHostedService<NotificationOrchestrator>();
        return services;
    }
}
