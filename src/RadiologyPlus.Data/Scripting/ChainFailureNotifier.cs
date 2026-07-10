using Microsoft.Extensions.Logging;
using RadiologyPlus.Notifications;
using RadiologyPlus.Scripting;

namespace RadiologyPlus.Data.Scripting;

/// <summary>
/// Queues an email when a chain run fails, via the notifications queue (the
/// orchestrator does the actual sending). Lives in the Data layer because it
/// composes the Scripting and Notifications stacks — the Scripting project only
/// sees the IChainFailureNotifier contract. Never throws: a notification
/// problem is logged, not propagated into the runner.
/// </summary>
public sealed class ChainFailureNotifier : IChainFailureNotifier
{
    private readonly INotificationService _notifications;
    private readonly INotificationAdminRepository _templates;
    private readonly ILogger<ChainFailureNotifier> _logger;

    public ChainFailureNotifier(
        INotificationService notifications,
        INotificationAdminRepository templates,
        ILogger<ChainFailureNotifier> logger)
    {
        _notifications = notifications;
        _templates = templates;
        _logger = logger;
    }

    public async Task NotifyAsync(ChainFailureContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            string? subject;
            string body;
            var isHtml = false;

            var template = context.TemplateId is { } templateId
                ? await _templates.GetTemplateAsync(context.TenantId, templateId, cancellationToken)
                : null;

            if (template is { IsActive: true })
            {
                var variables = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["chainName"] = context.ChainName,
                    ["chainRunId"] = context.ChainRunId,
                    ["stepsTotal"] = context.StepsTotal,
                    ["stepsSucceeded"] = context.StepsSucceeded,
                    ["stepsFailed"] = context.StepsFailed,
                    ["errorSummary"] = context.ErrorSummary ?? "",
                };
                subject = template.SubjectTemplate is null
                    ? null
                    : await _notifications.RenderAsync(template.SubjectTemplate, variables, cancellationToken);
                body = await _notifications.RenderAsync(template.BodyTemplate, variables, cancellationToken);
                isHtml = template.IsHtml;
            }
            else
            {
                if (context.TemplateId is not null)
                {
                    _logger.LogWarning(
                        "Chain {Chain} failure template {Template} is missing or inactive; using the built-in message.",
                        context.ChainId, context.TemplateId);
                }
                subject = $"Script chain \"{context.ChainName}\" failed";
                body =
                    $"Script chain \"{context.ChainName}\" failed (run #{context.ChainRunId}).\n\n" +
                    $"Steps: {context.StepsSucceeded} succeeded, {context.StepsFailed} failed of {context.StepsTotal}.\n\n" +
                    (string.IsNullOrWhiteSpace(context.ErrorSummary) ? "" : $"Details:\n{context.ErrorSummary}\n");
            }

            var notificationId = await _notifications.QueueAsync(new NotificationRequest(
                TenantId: context.TenantId,
                Channel: NotificationChannel.Email,
                Recipient: context.Recipient,
                Subject: subject,
                Body: body,
                IsHtml: isHtml,
                Priority: 3, // failures jump ahead of routine mail
                SourceType: "chain-failure",
                SourceId: context.ChainRunId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                TemplateId: template?.TemplateId), cancellationToken);

            _logger.LogInformation("Queued failure notification {Notification} for chain run {Run}.",
                notificationId, context.ChainRunId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Could not queue the failure notification for chain run {Run}.", context.ChainRunId);
        }
    }
}
