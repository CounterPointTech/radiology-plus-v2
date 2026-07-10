namespace RadiologyPlus.Notifications;

// ---------------------------------------------------------------------------
// Admin-facing records for the Notifications console (queue/templates/stats).
// The orchestrator keeps using the lean QueuedNotification; these carry the
// extra columns (source, template name, audit timestamps) the management UI
// needs. Channel/status travel as db-token strings ("email", "pending", ...)
// to match the wire format and avoid the enum-as-number JSON trap.
// ---------------------------------------------------------------------------

/// <summary>Queue list row — body omitted (fetch the detail for it).</summary>
public sealed record NotificationQueueItem(
    long NotificationId,
    Guid? TemplateId,
    string? TemplateName,
    string Channel,
    string Recipient,
    string? Subject,
    int Priority,
    string Status,
    int RetryCount,
    int MaxRetries,
    DateTimeOffset ScheduledAt,
    DateTimeOffset? SentAt,
    DateTimeOffset? FailedAt,
    string? LastError,
    string? SourceType,
    string? SourceId,
    DateTimeOffset CreatedAt);

public sealed record NotificationQueueDetail(
    long NotificationId,
    Guid? TemplateId,
    string? TemplateName,
    string Channel,
    string Recipient,
    string? Subject,
    string Body,
    bool IsHtml,
    int Priority,
    string Status,
    int RetryCount,
    int MaxRetries,
    DateTimeOffset ScheduledAt,
    DateTimeOffset? SentAt,
    DateTimeOffset? FailedAt,
    string? LastError,
    string? SourceType,
    string? SourceId,
    DateTimeOffset CreatedAt);

/// <summary>One page of the queue plus the unpaged filtered total.</summary>
public sealed record NotificationQueuePage(
    IReadOnlyList<NotificationQueueItem> Items,
    int Total);

/// <summary>Result of a cancel/retry: the row as it now stands, and whether it moved.</summary>
public sealed record NotificationQueueAction(
    bool Changed,
    NotificationQueueDetail Item);

/// <summary>Dashboard rollup. Pending/Sending/Failed are current; Sent24h is a window.</summary>
public sealed record NotificationStats(
    int Pending,
    int Sending,
    int Sent24h,
    int Failed,
    DateTimeOffset? OldestPendingAt,
    IReadOnlyList<NotificationChannelCount> ByChannel24h);

public sealed record NotificationChannelCount(string Channel, int Count);

public sealed record NotificationTemplateSummary(
    Guid TemplateId,
    string Name,
    string Channel,
    bool IsHtml,
    bool IsActive,
    DateTimeOffset CreatedAt);

public sealed record NotificationTemplateDetail(
    Guid TemplateId,
    string Name,
    string Channel,
    string? SubjectTemplate,
    string BodyTemplate,
    bool IsHtml,
    bool IsActive,
    DateTimeOffset CreatedAt);

/// <summary>Write-shape for template create/update (channel is a db token).</summary>
public sealed record NotificationTemplateSave(
    string Name,
    string Channel,
    string? SubjectTemplate,
    string BodyTemplate,
    bool IsHtml,
    bool IsActive);

/// <summary>
/// Management surface over notifications.queue / notifications.templates.
/// Tenant-scoped: every query passes tenant_id explicitly (RLS enforces it too).
/// </summary>
public interface INotificationAdminRepository
{
    /// <summary>status/channel null = no filter; both are db tokens.</summary>
    Task<NotificationQueuePage> ListQueueAsync(
        Guid tenantId, string? status, string? channel, int limit, int offset,
        CancellationToken cancellationToken = default);

    Task<NotificationQueueDetail?> GetQueueItemAsync(Guid tenantId, long notificationId, CancellationToken cancellationToken = default);

    /// <summary>pending -&gt; cancelled. Null when not found; Changed=false when not pending.</summary>
    Task<NotificationQueueAction?> CancelAsync(Guid tenantId, long notificationId, CancellationToken cancellationToken = default);

    /// <summary>failed/cancelled -&gt; pending (retry count reset, scheduled now). Null when not found; Changed=false when not retryable.</summary>
    Task<NotificationQueueAction?> RetryAsync(Guid tenantId, long notificationId, CancellationToken cancellationToken = default);

    Task<NotificationStats> GetStatsAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NotificationTemplateSummary>> ListTemplatesAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task<NotificationTemplateDetail?> GetTemplateAsync(Guid tenantId, Guid templateId, CancellationToken cancellationToken = default);
    Task<NotificationTemplateDetail> CreateTemplateAsync(Guid tenantId, NotificationTemplateSave input, CancellationToken cancellationToken = default);

    /// <summary>Throws KeyNotFoundException when the template does not exist.</summary>
    Task<NotificationTemplateDetail> UpdateTemplateAsync(Guid tenantId, Guid templateId, NotificationTemplateSave input, CancellationToken cancellationToken = default);

    /// <summary>Throws KeyNotFoundException when the template does not exist.</summary>
    Task<NotificationTemplateDetail> SetTemplateActiveAsync(Guid tenantId, Guid templateId, bool isActive, CancellationToken cancellationToken = default);

    /// <summary>Returns false when no row was removed. FK violation (send history) surfaces as PostgresException.</summary>
    Task<bool> DeleteTemplateAsync(Guid tenantId, Guid templateId, CancellationToken cancellationToken = default);
}

/// <summary>Db-token mapping for the notification enums ("email", "pending", ...).</summary>
public static class NotificationTokens
{
    public static string ToDbToken(this NotificationChannel channel) => channel switch
    {
        NotificationChannel.Email => "email",
        NotificationChannel.Teams => "teams",
        NotificationChannel.Sms => "sms",
        NotificationChannel.Webhook => "webhook",
        _ => "email",
    };

    /// <summary>Throws ArgumentException on an unknown token.</summary>
    public static NotificationChannel ParseChannel(string token) => token switch
    {
        "email" => NotificationChannel.Email,
        "teams" => NotificationChannel.Teams,
        "sms" => NotificationChannel.Sms,
        "webhook" => NotificationChannel.Webhook,
        _ => throw new ArgumentException($"Unknown notification channel \"{token}\".", nameof(token)),
    };

    public static bool IsValidChannel(string token) =>
        token is "email" or "teams" or "sms" or "webhook";

    public static string ToDbToken(this NotificationStatus status) => status switch
    {
        NotificationStatus.Pending => "pending",
        NotificationStatus.Sending => "sending",
        NotificationStatus.Sent => "sent",
        NotificationStatus.Failed => "failed",
        NotificationStatus.Cancelled => "cancelled",
        _ => "pending",
    };

    public static bool IsValidStatus(string token) =>
        token is "pending" or "sending" or "sent" or "failed" or "cancelled";
}
