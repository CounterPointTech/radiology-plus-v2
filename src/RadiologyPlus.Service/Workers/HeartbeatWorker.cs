using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RadiologyPlus.Service.Workers;

public sealed class HeartbeatWorker(ILogger<HeartbeatWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("RadiologyPlus.Service starting.");
        while (!stoppingToken.IsCancellationRequested)
        {
            logger.LogDebug("Heartbeat at {Time}", DateTimeOffset.UtcNow);
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
