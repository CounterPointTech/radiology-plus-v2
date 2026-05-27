using Microsoft.Extensions.Logging;

namespace RadiologyPlus.Notifications.Channels;

/// <summary>
/// Fallback channel that just logs the notification. Useful for dev/test or when a real
/// channel hasn't been configured yet for a given tenant.
/// </summary>
public sealed class LogOnlyChannel : INotificationChannelSender
{
    private readonly ILogger<LogOnlyChannel> _logger;

    public LogOnlyChannel(ILogger<LogOnlyChannel> logger) => _logger = logger;

    public NotificationChannel Channel => NotificationChannel.Email;

    public Task<NotificationDeliveryResult> SendAsync(QueuedNotification notification, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[LogOnly] {Channel} to {Recipient} — Subject: {Subject}",
            notification.Channel, notification.Recipient, notification.Subject ?? "(none)");
        _logger.LogDebug("[LogOnly] Body: {Body}", notification.Body);
        return Task.FromResult(new NotificationDeliveryResult(Success: true, Error: null));
    }
}
