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
/// </summary>
public sealed class GraphEmailChannel : INotificationChannelSender
{
    private readonly GraphEmailOptions _options;
    private readonly ILogger<GraphEmailChannel> _logger;
    private readonly Lazy<IConfidentialClientApplication> _msal;
    private GraphServiceClient? _graph;
    private static readonly string[] Scopes = ["https://graph.microsoft.com/.default"];

    public GraphEmailChannel(IOptions<GraphEmailOptions> options, ILogger<GraphEmailChannel> logger)
    {
        _options = options.Value;
        _logger = logger;
        _msal = new Lazy<IConfidentialClientApplication>(BuildMsal);
    }

    public NotificationChannel Channel => NotificationChannel.Email;

    public async Task<NotificationDeliveryResult> SendAsync(QueuedNotification notification, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured())
        {
            return new NotificationDeliveryResult(false, "Graph email channel is not configured (TenantId/ClientId/ClientSecret/FromAddress required).");
        }

        try
        {
            var token = await _msal.Value.AcquireTokenForClient(Scopes).ExecuteAsync(cancellationToken);
            _graph ??= new GraphServiceClient(new SimpleAuthProvider(token.AccessToken));

            var message = new Message
            {
                Subject = notification.Subject ?? "(no subject)",
                Body = new ItemBody
                {
                    ContentType = notification.IsHtml ? BodyType.Html : BodyType.Text,
                    Content = notification.Body,
                },
                ToRecipients =
                [
                    new Recipient { EmailAddress = new EmailAddress { Address = notification.Recipient } }
                ],
            };

            await _graph.Users[_options.FromAddress]
                .SendMail
                .PostAsync(new SendMailPostRequestBody { Message = message, SaveToSentItems = false }, cancellationToken: cancellationToken);

            return new NotificationDeliveryResult(true, null);
        }
        catch (MsalServiceException ex)
        {
            _logger.LogError(ex, "Graph auth failed for notification {Id}.", notification.NotificationId);
            return new NotificationDeliveryResult(false, $"Auth failure: {ex.Message}");
        }
        catch (Microsoft.Graph.Models.ODataErrors.ODataError ex)
        {
            _logger.LogError(ex, "Graph send failed for notification {Id}.", notification.NotificationId);
            return new NotificationDeliveryResult(false, ex.Message);
        }
    }

    private bool IsConfigured()
        => !string.IsNullOrWhiteSpace(_options.TenantId)
            && !string.IsNullOrWhiteSpace(_options.ClientId)
            && !string.IsNullOrWhiteSpace(_options.ClientSecret)
            && !string.IsNullOrWhiteSpace(_options.FromAddress);

    private IConfidentialClientApplication BuildMsal()
    {
        return ConfidentialClientApplicationBuilder.Create(_options.ClientId)
            .WithClientSecret(_options.ClientSecret)
            .WithAuthority($"https://login.microsoftonline.com/{_options.TenantId}")
            .Build();
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
