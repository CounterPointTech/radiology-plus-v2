using System.Text.Json;
using HandlebarsDotNet;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Npgsql;
using RadiologyPlus.Core.Audit;
using RadiologyPlus.Core.Identity;
using RadiologyPlus.Notifications;
using RadiologyPlus.Notifications.Channels;

namespace RadiologyPlus.AdminApi.Endpoints;

/// <summary>
/// Notifications console surface (NRS/Admin, enforced per-handler; every mutation
/// audited). Queue browse/cancel/retry + stats, template CRUD with Handlebars
/// preview, ad-hoc compose, and the per-tenant Graph email settings. Actual
/// sending happens in RadiologyPlus.AdminService's orchestrator — the UI polls
/// the queue to watch a message move pending → sending → sent.
/// </summary>
public static class NotificationsEndpoints
{
    public static IEndpointRouteBuilder MapNotificationsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/notifications")
            .WithTags("Notifications")
            .RequireAuthorization();

        group.MapGet("/stats", GetStatsAsync).WithName("NotificationsStats");
        group.MapGet("/queue", ListQueueAsync).WithName("NotificationsQueue");
        group.MapGet("/queue/{notificationId:long}", GetQueueItemAsync).WithName("NotificationsQueueItem");
        group.MapPost("/queue/{notificationId:long}/cancel", CancelAsync).WithName("NotificationsCancel");
        group.MapPost("/queue/{notificationId:long}/retry", RetryAsync).WithName("NotificationsRetry");
        group.MapPost("/compose", ComposeAsync).WithName("NotificationsCompose");

        group.MapGet("/templates", ListTemplatesAsync).WithName("NotificationsTemplates");
        group.MapPost("/templates", CreateTemplateAsync).WithName("NotificationsTemplateCreate");
        group.MapPost("/templates/preview", PreviewTemplateAsync).WithName("NotificationsTemplatePreview");
        group.MapGet("/templates/{templateId:guid}", GetTemplateAsync).WithName("NotificationsTemplateGet");
        group.MapPut("/templates/{templateId:guid}", UpdateTemplateAsync).WithName("NotificationsTemplateUpdate");
        group.MapPatch("/templates/{templateId:guid}/active", SetTemplateActiveAsync).WithName("NotificationsTemplateSetActive");
        group.MapDelete("/templates/{templateId:guid}", DeleteTemplateAsync).WithName("NotificationsTemplateDelete");

        group.MapGet("/settings/graph", GetGraphSettingsAsync).WithName("NotificationsGraphSettings");
        group.MapPut("/settings/graph", SaveGraphSettingsAsync).WithName("NotificationsGraphSettingsSave");
        group.MapDelete("/settings/graph", DeleteGraphSettingsAsync).WithName("NotificationsGraphSettingsDelete");
        group.MapPost("/settings/graph/test", TestGraphSettingsAsync).WithName("NotificationsGraphSettingsTest");

        return app;
    }

    // -- Queue -----------------------------------------------------------------

    private static async Task<IResult> GetStatsAsync(
        ICurrentUser currentUser, INotificationAdminRepository repo, CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();
        return Results.Ok(await repo.GetStatsAsync(user.TenantId, ct));
    }

    private static async Task<IResult> ListQueueAsync(
        [FromQuery] string? status,
        [FromQuery] string? channel,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        ICurrentUser currentUser,
        INotificationAdminRepository repo,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();

        var statusToken = Normalize(status);
        if (statusToken is not null && !NotificationTokens.IsValidStatus(statusToken))
            return Results.BadRequest(new { error = "status must be one of: pending, sending, sent, failed, cancelled." });
        var channelToken = Normalize(channel);
        if (channelToken is not null && !NotificationTokens.IsValidChannel(channelToken))
            return Results.BadRequest(new { error = "channel must be one of: email, teams, sms, webhook." });

        var page = await repo.ListQueueAsync(
            user.TenantId, statusToken, channelToken,
            Math.Clamp(limit ?? 50, 1, 200), Math.Max(offset ?? 0, 0), ct);
        return Results.Ok(page);
    }

    private static async Task<IResult> GetQueueItemAsync(
        long notificationId, ICurrentUser currentUser, INotificationAdminRepository repo, CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();
        var item = await repo.GetQueueItemAsync(user.TenantId, notificationId, ct);
        return item is null ? Results.NotFound(new { error = "Notification not found." }) : Results.Ok(item);
    }

    private static async Task<IResult> CancelAsync(
        long notificationId,
        ICurrentUser currentUser,
        INotificationAdminRepository repo,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();

        var result = await repo.CancelAsync(user.TenantId, notificationId, ct);
        if (result is null) return Results.NotFound(new { error = "Notification not found." });

        if (result.Changed)
        {
            await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Update,
                $"notifications.queue notification_id={notificationId}: cancelled", http, ct);
        }

        return Results.Ok(new
        {
            result.Changed,
            result.Item,
            Message = result.Changed
                ? "Notification cancelled."
                : $"Only a pending notification can be cancelled — this one is {result.Item.Status}.",
        });
    }

    private static async Task<IResult> RetryAsync(
        long notificationId,
        ICurrentUser currentUser,
        INotificationAdminRepository repo,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();

        var result = await repo.RetryAsync(user.TenantId, notificationId, ct);
        if (result is null) return Results.NotFound(new { error = "Notification not found." });

        if (result.Changed)
        {
            await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Update,
                $"notifications.queue notification_id={notificationId}: requeued for retry", http, ct);
        }

        return Results.Ok(new
        {
            result.Changed,
            result.Item,
            Message = result.Changed
                ? "Notification requeued — the orchestrator will pick it up within its next poll."
                : $"Only a failed or cancelled notification can be retried — this one is {result.Item.Status}.",
        });
    }

    // -- Compose -----------------------------------------------------------------

    private static async Task<IResult> ComposeAsync(
        NotificationComposeRequest req,
        ICurrentUser currentUser,
        INotificationAdminRepository repo,
        INotificationService notifications,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();

        if (req is null) return Results.BadRequest(new { error = "Request body is required." });
        if (string.IsNullOrWhiteSpace(req.Recipient))
            return Results.BadRequest(new { error = "recipient is required." });
        var priority = req.Priority ?? 5;
        if (priority is < 1 or > 10)
            return Results.BadRequest(new { error = "priority must be between 1 and 10." });

        NotificationChannel channel;
        string? subject;
        string body;
        bool isHtml;

        if (req.TemplateId is { } templateId)
        {
            var template = await repo.GetTemplateAsync(user.TenantId, templateId, ct);
            if (template is null) return Results.NotFound(new { error = "Template not found." });
            if (!template.IsActive) return Results.BadRequest(new { error = "Template is inactive — activate it before sending from it." });

            var variables = ToPlainDictionary(req.Variables);
            try
            {
                subject = template.SubjectTemplate is null
                    ? null
                    : await notifications.RenderAsync(template.SubjectTemplate, variables, ct);
                body = await notifications.RenderAsync(template.BodyTemplate, variables, ct);
            }
            catch (HandlebarsException ex)
            {
                return Results.BadRequest(new { error = $"Template failed to render: {ex.Message}" });
            }

            channel = NotificationTokens.ParseChannel(template.Channel);
            isHtml = template.IsHtml;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(req.Body))
                return Results.BadRequest(new { error = "body is required (or send from a template)." });

            var channelToken = Normalize(req.Channel) ?? "email";
            if (!NotificationTokens.IsValidChannel(channelToken))
                return Results.BadRequest(new { error = "channel must be one of: email, teams, sms, webhook." });
            channel = NotificationTokens.ParseChannel(channelToken);
            subject = string.IsNullOrWhiteSpace(req.Subject) ? null : req.Subject.Trim();
            body = req.Body;
            isHtml = req.IsHtml;
        }

        var id = await notifications.QueueAsync(new NotificationRequest(
            TenantId: user.TenantId,
            Channel: channel,
            Recipient: req.Recipient.Trim(),
            Subject: subject,
            Body: body,
            IsHtml: isHtml,
            Priority: priority,
            SourceType: "console-compose",
            SourceId: user.UserId.ToString(),
            TemplateId: req.TemplateId), ct);

        await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Create,
            $"notifications.queue notification_id={id}: composed {channel.ToDbToken()} to {req.Recipient.Trim()}" +
            (req.TemplateId is null ? "" : $" from template {req.TemplateId}"),
            http, ct);

        var item = await repo.GetQueueItemAsync(user.TenantId, id, ct);
        return Results.Ok(item);
    }

    // -- Templates ------------------------------------------------------------------

    private static async Task<IResult> ListTemplatesAsync(
        ICurrentUser currentUser, INotificationAdminRepository repo, CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();
        return Results.Ok(await repo.ListTemplatesAsync(user.TenantId, ct));
    }

    private static async Task<IResult> GetTemplateAsync(
        Guid templateId, ICurrentUser currentUser, INotificationAdminRepository repo, CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();
        var template = await repo.GetTemplateAsync(user.TenantId, templateId, ct);
        return template is null ? Results.NotFound(new { error = "Template not found." }) : Results.Ok(template);
    }

    private static async Task<IResult> CreateTemplateAsync(
        NotificationTemplateSaveRequest req,
        ICurrentUser currentUser,
        INotificationAdminRepository repo,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();
        if (ValidateTemplate(req, out var input) is { } bad) return bad;

        try
        {
            var created = await repo.CreateTemplateAsync(user.TenantId, input, ct);
            await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Create,
                $"notifications.templates template_id={created.TemplateId}: created '{created.Name}' ({created.Channel}) active={created.IsActive}",
                http, ct);
            return Results.Ok(created);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Results.Conflict(new { error = $"A template named \"{req.Name}\" already exists." });
        }
    }

    private static async Task<IResult> UpdateTemplateAsync(
        Guid templateId,
        NotificationTemplateSaveRequest req,
        ICurrentUser currentUser,
        INotificationAdminRepository repo,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();
        if (ValidateTemplate(req, out var input) is { } bad) return bad;

        try
        {
            var updated = await repo.UpdateTemplateAsync(user.TenantId, templateId, input, ct);
            await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Update,
                $"notifications.templates template_id={templateId}: updated '{updated.Name}'", http, ct);
            return Results.Ok(updated);
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { error = "Template not found." });
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Results.Conflict(new { error = $"A template named \"{req.Name}\" already exists." });
        }
    }

    private static async Task<IResult> SetTemplateActiveAsync(
        Guid templateId,
        TemplateActiveRequest req,
        ICurrentUser currentUser,
        INotificationAdminRepository repo,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();

        try
        {
            var updated = await repo.SetTemplateActiveAsync(user.TenantId, templateId, req.IsActive, ct);
            await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Update,
                $"notifications.templates template_id={templateId}: active->{req.IsActive}", http, ct);
            return Results.Ok(updated);
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { error = "Template not found." });
        }
    }

    private static async Task<IResult> DeleteTemplateAsync(
        Guid templateId,
        ICurrentUser currentUser,
        INotificationAdminRepository repo,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();

        try
        {
            var removed = await repo.DeleteTemplateAsync(user.TenantId, templateId, ct);
            if (!removed) return Results.NotFound(new { error = "Template not found." });
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            return Results.Conflict(new { error = "This template has send history — deactivate it instead of deleting." });
        }

        await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Delete,
            $"notifications.templates template_id={templateId} removed", http, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> PreviewTemplateAsync(
        TemplatePreviewRequest req,
        ICurrentUser currentUser,
        INotificationService notifications,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();
        if (req is null || string.IsNullOrEmpty(req.BodyTemplate))
            return Results.BadRequest(new { error = "bodyTemplate is required." });

        var variables = ToPlainDictionary(req.Variables);
        try
        {
            var subject = string.IsNullOrEmpty(req.SubjectTemplate)
                ? null
                : await notifications.RenderAsync(req.SubjectTemplate, variables, ct);
            var body = await notifications.RenderAsync(req.BodyTemplate, variables, ct);
            return Results.Ok(new { subject, body });
        }
        catch (HandlebarsException ex)
        {
            return Results.BadRequest(new { error = $"Template failed to render: {ex.Message}" });
        }
    }

    // -- Graph email settings ----------------------------------------------------------

    private static async Task<IResult> GetGraphSettingsAsync(
        ICurrentUser currentUser, IGraphEmailSettingsStore store, CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();
        var settings = await store.GetAsync(user.TenantId, ct);
        return Results.Ok(new { configured = settings is not null, settings });
    }

    private static async Task<IResult> SaveGraphSettingsAsync(
        GraphSettingsSaveRequest req,
        ICurrentUser currentUser,
        IGraphEmailSettingsStore store,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();

        if (req is null) return Results.BadRequest(new { error = "Request body is required." });
        if (string.IsNullOrWhiteSpace(req.GraphTenantId))
            return Results.BadRequest(new { error = "graphTenantId is required." });
        if (string.IsNullOrWhiteSpace(req.ClientId))
            return Results.BadRequest(new { error = "clientId is required." });
        if (string.IsNullOrWhiteSpace(req.FromAddress))
            return Results.BadRequest(new { error = "fromAddress is required." });

        var secret = string.IsNullOrWhiteSpace(req.ClientSecret) ? null : req.ClientSecret;
        if (secret is null && await store.GetAsync(user.TenantId, ct) is null)
            return Results.BadRequest(new { error = "clientSecret is required on first setup." });

        await store.UpsertAsync(user.TenantId, new GraphEmailSettingsUpsert(
            req.GraphTenantId.Trim(), req.ClientId.Trim(), secret, req.FromAddress.Trim()), ct);

        await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Update,
            $"tenancy.graph_email_connections: saved (from={req.FromAddress.Trim()}, secret {(secret is null ? "kept" : "replaced")})",
            http, ct);

        var settings = await store.GetAsync(user.TenantId, ct);
        return Results.Ok(new { configured = true, settings });
    }

    private static async Task<IResult> DeleteGraphSettingsAsync(
        ICurrentUser currentUser,
        IGraphEmailSettingsStore store,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();

        var removed = await store.DeleteAsync(user.TenantId, ct);
        if (!removed) return Results.NotFound(new { error = "No Graph email settings are configured." });

        await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Delete,
            "tenancy.graph_email_connections: removed", http, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> TestGraphSettingsAsync(
        GraphTestRequest? req,
        ICurrentUser currentUser,
        IGraphEmailSettingsStore store,
        GraphEmailChannel channel,
        IOptions<GraphEmailOptions> fallback,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();

        // Test what a real send would use: the tenant's saved settings, else the
        // appsettings fallback. SendTestAsync reports "not configured" when both are empty.
        var options = await store.ResolveAsync(user.TenantId, ct) ?? fallback.Value;
        var recipient = string.IsNullOrWhiteSpace(req?.Recipient) ? null : req.Recipient.Trim();
        var result = await channel.SendTestAsync(options, recipient, ct);

        await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Execute,
            $"notifications graph settings test: {(result.Success ? "success" : "failed")}" +
            (recipient is null ? " (token only)" : $" (test mail to {recipient})"),
            http, ct);

        return Results.Ok(new
        {
            Ok = result.Success,
            result.Error,
            SentTestEmail = result.Success && recipient is not null,
        });
    }

    // -- Validation + helpers -------------------------------------------------------------

    private static string? Normalize(string? token) =>
        string.IsNullOrWhiteSpace(token) ? null : token.Trim().ToLowerInvariant();

    private static IResult? ValidateTemplate(NotificationTemplateSaveRequest req, out NotificationTemplateSave input)
    {
        input = null!;
        if (req is null) return Results.BadRequest(new { error = "Request body is required." });
        if (string.IsNullOrWhiteSpace(req.Name))
            return Results.BadRequest(new { error = "name is required." });
        if (string.IsNullOrWhiteSpace(req.BodyTemplate))
            return Results.BadRequest(new { error = "bodyTemplate is required." });

        var channel = Normalize(req.Channel) ?? "email";
        if (!NotificationTokens.IsValidChannel(channel))
            return Results.BadRequest(new { error = "channel must be one of: email, teams, sms, webhook." });

        input = new NotificationTemplateSave(
            req.Name.Trim(),
            channel,
            string.IsNullOrWhiteSpace(req.SubjectTemplate) ? null : req.SubjectTemplate,
            req.BodyTemplate,
            req.IsHtml,
            req.IsActive);
        return null;
    }

    /// <summary>Handlebars renders CLR primitives cleanly; JsonElements it does not — unwrap them.</summary>
    private static Dictionary<string, object?> ToPlainDictionary(Dictionary<string, JsonElement>? variables)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (variables is null) return result;
        foreach (var (key, value) in variables)
            result[key] = ToPlain(value);
        return result;
    }

    private static object? ToPlain(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => element.EnumerateArray().Select(ToPlain).ToList(),
        JsonValueKind.Object => element.EnumerateObject()
            .ToDictionary(p => p.Name, p => ToPlain(p.Value), StringComparer.Ordinal),
        _ => null,
    };
}

public sealed record NotificationComposeRequest(
    string? Channel,
    string Recipient,
    string? Subject,
    string? Body,
    bool IsHtml,
    int? Priority,
    Guid? TemplateId,
    Dictionary<string, JsonElement>? Variables);

public sealed record NotificationTemplateSaveRequest(
    string Name,
    string? Channel,
    string? SubjectTemplate,
    string BodyTemplate,
    bool IsHtml,
    bool IsActive);

public sealed record TemplateActiveRequest(bool IsActive);

public sealed record TemplatePreviewRequest(
    string? SubjectTemplate,
    string BodyTemplate,
    Dictionary<string, JsonElement>? Variables);

public sealed record GraphSettingsSaveRequest(
    string GraphTenantId,
    string ClientId,
    string? ClientSecret,
    string FromAddress);

public sealed record GraphTestRequest(string? Recipient);
