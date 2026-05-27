using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RadiologyPlus.Notifications;

/// <summary>
/// Background worker that polls notifications.queue, picks up pending rows, and dispatches
/// each to the configured channel sender. Retries with exponential backoff up to max_retries.
/// </summary>
public sealed class NotificationOrchestrator : BackgroundService
{
    private readonly INotificationRepository _repository;
    private readonly Dictionary<NotificationChannel, INotificationChannelSender> _channels;
    private readonly ILogger<NotificationOrchestrator> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(15);
    private const int BatchSize = 20;

    public NotificationOrchestrator(
        INotificationRepository repository,
        IEnumerable<INotificationChannelSender> channels,
        ILogger<NotificationOrchestrator> logger)
    {
        _repository = repository;
        _logger = logger;
        // If multiple senders register for the same channel, last one wins — production should DI
        // a single sender per channel (e.g. Graph for Email, Teams webhook for Teams).
        _channels = channels.ToDictionary(c => c.Channel);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("NotificationOrchestrator started; polling every {Seconds}s.", _pollInterval.TotalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Notification orchestration tick failed.");
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProcessOnceAsync(CancellationToken cancellationToken)
    {
        var batch = await _repository.DequeueBatchAsync(BatchSize, cancellationToken);
        if (batch.Count == 0) return;

        _logger.LogDebug("Processing {Count} pending notification(s).", batch.Count);

        foreach (var n in batch)
        {
            if (!_channels.TryGetValue(n.Channel, out var sender))
            {
                _logger.LogWarning("No channel registered for {Channel}; marking failed.", n.Channel);
                await _repository.MarkFailedAsync(n.NotificationId, $"No sender for {n.Channel}", shouldRetry: false, cancellationToken);
                continue;
            }

            await _repository.MarkSendingAsync(n.NotificationId, cancellationToken);
            NotificationDeliveryResult result;
            try
            {
                result = await sender.SendAsync(n, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                result = new NotificationDeliveryResult(false, ex.Message);
            }
            catch (TaskCanceledException ex)
            {
                result = new NotificationDeliveryResult(false, ex.Message);
            }

            if (result.Success)
            {
                await _repository.MarkSentAsync(n.NotificationId, cancellationToken);
            }
            else
            {
                var canRetry = n.RetryCount < n.MaxRetries;
                await _repository.MarkFailedAsync(n.NotificationId, result.Error ?? "unknown error", canRetry, cancellationToken);
            }
        }
    }
}
