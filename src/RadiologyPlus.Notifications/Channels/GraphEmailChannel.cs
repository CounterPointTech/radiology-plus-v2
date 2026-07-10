using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using Microsoft.Identity.Client;

namespace RadiologyPlus.Notifications.Channels;

public sealed class GraphEmailOptions
{
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    /// <summary>Default From: address — the mailbox the app sends as.</summary>
    public string FromAddress { get; set; } = "";
}

/// <summary>
/// Microsoft Graph (Office 365) email channel using client-credentials OAuth.
/// Credentials resolve per tenant at send time (tenancy.graph_email_connections);
/// the appsettings-bound options are the dev/single-tenant fallback. A Graph client
/// is built per send with a fresh token — never cached across sends, so an expired
/// token can't get reused.
/// </summary>
public sealed class GraphEmailChannel : INotificationChannelSender
{
    private readonly IGraphEmailSettingsStore _settings;
    private readonly GraphEmailOptions _fallback;
    private readonly ILogger<GraphEmailChannel> _logger;
    // MSAL apps cache tokens internally — keep one per credential set so a batch of
    // sends doesn't hit the token endpoint every time. Key includes the secret so a
    // rotated secret naturally maps to a fresh app.
    private readonly ConcurrentDictionary<string, IConfidentialClientApplication> _msalApps = new();
    private static readonly string[] Scopes = ["https://graph.microsoft.com/.default"];

    public GraphEmailChannel(
        IGraphEmailSettingsStore settings,
        IOptions<GraphEmailOptions> fallbackOptions,
        ILogger<GraphEmailChannel> logger)
    {
        _settings = settings;
        _fallback = fallbackOptions.Value;
        _logger = logger;
    }

    public NotificationChannel Channel => NotificationChannel.Email;

    public async Task<NotificationDeliveryResult> SendAsync(QueuedNotification notification, CancellationToken cancellationToken = default)
    {
        GraphEmailOptions? options;
        try
        {
            options = await _settings.ResolveAsync(notification.TenantId, cancellationToken);
        }
        // Boundary guard: a resolution failure (DB down, undecryptable secret) must
        // surface as a failed delivery — anything escaping SendAsync besides the
        // handled HTTP exceptions would kill the orchestrator loop.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to resolve Graph email settings for tenant {Tenant} (notification {Id}).",
                notification.TenantId, notification.NotificationId);
            return new NotificationDeliveryResult(false, $"Could not resolve email settings: {ex.Message}");
        }

        options ??= IsConfigured(_fallback) ? _fallback : null;
        if (options is null || !IsConfigured(options))
        {
            return new NotificationDeliveryResult(false,
                "Graph email is not configured for this tenant (and no appsettings fallback is set). Configure it under Notifications → Email settings.");
        }

        return await SendMailAsync(options, notification.Recipient, notification.Subject, notification.Body, notification.IsHtml,
            $"notification {notification.NotificationId}", cancellationToken);
    }

    /// <summary>
    /// Settings-page test: validates the credentials by acquiring a token, and when
    /// <paramref name="testRecipient"/> is set also sends a real test message.
    /// </summary>
    public async Task<NotificationDeliveryResult> SendTestAsync(
        GraphEmailOptions options, string? testRecipient, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!IsConfigured(options))
            return new NotificationDeliveryResult(false, "TenantId, ClientId, ClientSecret and FromAddress are all required.");

        if (string.IsNullOrWhiteSpace(testRecipient))
        {
            try
            {
                await GetMsal(options).AcquireTokenForClient(Scopes).ExecuteAsync(cancellationToken);
                return new NotificationDeliveryResult(true, null);
            }
            catch (MsalException ex)
            {
                _logger.LogWarning(ex, "Graph email settings test failed (token acquisition).");
                return new NotificationDeliveryResult(false, $"Auth failure: {ex.Message}");
            }
        }

        return await SendMailAsync(options, testRecipient,
            "Radiology Plus — test notification",
            "This is a test message from the Radiology Plus notifications console. If you're reading it, the Graph email settings work.",
            isHtml: false, context: "settings test", cancellationToken);
    }

    private async Task<NotificationDeliveryResult> SendMailAsync(
        GraphEmailOptions options, string recipient, string? subject, string body, bool isHtml,
        string context, CancellationToken cancellationToken)
    {
        try
        {
            var token = await GetMsal(options).AcquireTokenForClient(Scopes).ExecuteAsync(cancellationToken);
            using var graph = new GraphServiceClient(new SimpleAuthProvider(token.AccessToken));

            var message = new Message
            {
                Subject = subject ?? "(no subject)",
                Body = new ItemBody
                {
                    ContentType = isHtml ? BodyType.Html : BodyType.Text,
                    Content = body,
                },
                ToRecipients =
                [
                    new Recipient { EmailAddress = new EmailAddress { Address = recipient } }
                ],
            };

            await graph.Users[options.FromAddress]
                .SendMail
                .PostAsync(new SendMailPostRequestBody { Message = message, SaveToSentItems = false }, cancellationToken: cancellationToken);

            return new NotificationDeliveryResult(true, null);
        }
        catch (MsalException ex)
        {
            _logger.LogError(ex, "Graph auth failed for {Context}.", context);
            return new NotificationDeliveryResult(false, $"Auth failure: {ex.Message}");
        }
        catch (Microsoft.Graph.Models.ODataErrors.ODataError ex)
        {
            _logger.LogError(ex, "Graph send failed for {Context}.", context);
            return new NotificationDeliveryResult(false, ex.Error?.Message ?? ex.Message);
        }
    }

    private static bool IsConfigured(GraphEmailOptions options)
        => !string.IsNullOrWhiteSpace(options.TenantId)
            && !string.IsNullOrWhiteSpace(options.ClientId)
            && !string.IsNullOrWhiteSpace(options.ClientSecret)
            && !string.IsNullOrWhiteSpace(options.FromAddress);

    private IConfidentialClientApplication GetMsal(GraphEmailOptions options)
    {
        var key = $"{options.TenantId}|{options.ClientId}|{options.ClientSecret}";
        return _msalApps.GetOrAdd(key, _ =>
            ConfidentialClientApplicationBuilder.Create(options.ClientId)
                .WithClientSecret(options.ClientSecret)
                .WithAuthority($"https://login.microsoftonline.com/{options.TenantId}")
                .Build());
    }

    private sealed class SimpleAuthProvider : Microsoft.Kiota.Abstractions.Authentication.IAuthenticationProvider
    {
        private readonly string _token;
        public SimpleAuthProvider(string token) => _token = token;

        public Task AuthenticateRequestAsync(
            Microsoft.Kiota.Abstractions.RequestInformation request,
            Dictionary<string, object>? additionalAuthenticationContext = null,
            CancellationToken cancellationToken = default)
        {
            request.Headers.Add("Authorization", $"Bearer {_token}");
            return Task.CompletedTask;
        }
    }
}
