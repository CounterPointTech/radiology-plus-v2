namespace RadiologyPlus.Notifications;

public enum NotificationChannel
{
    Email = 1,
    Teams = 2,
    Sms = 3,
    Webhook = 4,
}

public enum NotificationStatus
{
    Pending = 1,
    Sending = 2,
    Sent = 3,
    Failed = 4,
    Cancelled = 5,
}

public sealed record NotificationRequest(
    Guid TenantId,
    NotificationChannel Channel,
    string Recipient,
    string? Subject,
    string Body,
    bool IsHtml,
    int Priority = 5,
    string? SourceType = null,
    string? SourceId = null,
    Guid? TemplateId = null);

public sealed record QueuedNotification(
    long NotificationId,
    Guid TenantId,
    Guid? TemplateId,
    NotificationChannel Channel,
    string Recipient,
    string? Subject,
    string Body,
    bool IsHtml,
    int Priority,
    NotificationStatus Status,
    int RetryCount,
    int MaxRetries,
    DateTimeOffset ScheduledAt,
    DateTimeOffset? SentAt,
    string? LastError);

public interface INotificationService
{
    Task<long> QueueAsync(NotificationRequest request, CancellationToken cancellationToken = default);
    Task<string> RenderAsync(string templateText, IReadOnlyDictionary<string, object?> variables, CancellationToken cancellationToken = default);
}

public interface INotificationRepository
{
    Task<long> EnqueueAsync(NotificationRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QueuedNotification>> DequeueBatchAsync(int batchSize, CancellationToken cancellationToken = default);
    Task MarkSendingAsync(long notificationId, CancellationToken cancellationToken = default);
    Task MarkSentAsync(long notificationId, CancellationToken cancellationToken = default);
    Task MarkFailedAsync(long notificationId, string errorMessage, bool shouldRetry, CancellationToken cancellationToken = default);
}

public sealed record NotificationDeliveryResult(bool Success, string? Error);

public interface INotificationChannelSender
{
    NotificationChannel Channel { get; }
    Task<NotificationDeliveryResult> SendAsync(QueuedNotification notification, CancellationToken cancellationToken = default);
}
