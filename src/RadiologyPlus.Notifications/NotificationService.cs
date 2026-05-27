using HandlebarsDotNet;
using Microsoft.Extensions.Logging;

namespace RadiologyPlus.Notifications;

public sealed class NotificationService : INotificationService
{
    private readonly INotificationRepository _repository;
    private readonly IHandlebars _handlebars;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(INotificationRepository repository, ILogger<NotificationService> logger)
    {
        _repository = repository;
        _logger = logger;
        _handlebars = Handlebars.Create();
    }

    public Task<long> QueueAsync(NotificationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Recipient))
            throw new ArgumentException("Recipient is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Body))
            throw new ArgumentException("Body is required.", nameof(request));

        _logger.LogDebug("Queuing {Channel} notification to {Recipient} for tenant {Tenant}.",
            request.Channel, request.Recipient, request.TenantId);

        return _repository.EnqueueAsync(request, cancellationToken);
    }

    public Task<string> RenderAsync(string templateText, IReadOnlyDictionary<string, object?> variables, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(templateText)) return Task.FromResult(templateText);
        var compiled = _handlebars.Compile(templateText);
        return Task.FromResult(compiled(variables));
    }
}
