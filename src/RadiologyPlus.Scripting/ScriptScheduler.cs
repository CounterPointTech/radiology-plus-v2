using Cronos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RadiologyPlus.Scripting;

/// <summary>
/// Background service that evaluates active scripts every minute. If a script's cron
/// expression matches the current minute and it hasn't been run for this slot yet,
/// it's invoked via the ScriptExecutionEngine.
/// </summary>
public sealed class ScriptScheduler : BackgroundService
{
    private readonly IScriptRepository _scripts;
    private readonly ScriptExecutionEngine _engine;
    private readonly ILogger<ScriptScheduler> _logger;
    private DateTimeOffset _lastTick = DateTimeOffset.MinValue;

    public ScriptScheduler(
        IScriptRepository scripts,
        ScriptExecutionEngine engine,
        ILogger<ScriptScheduler> logger)
    {
        _scripts = scripts;
        _engine = engine;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ScriptScheduler starting; ticking every minute.");
        // Align to the next minute boundary
        var delay = TimeSpan.FromSeconds(60 - DateTimeOffset.UtcNow.Second);
        try
        {
            await Task.Delay(delay, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is InvalidOperationException or CronFormatException)
            {
                _logger.LogError(ex, "ScriptScheduler tick failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var lastTick = _lastTick == DateTimeOffset.MinValue ? now.AddMinutes(-1) : _lastTick;
        _lastTick = now;

        var scripts = await _scripts.ListActiveAsync(cancellationToken);
        foreach (var script in scripts)
        {
            if (string.IsNullOrWhiteSpace(script.CronExpression)) continue;

            if (!CronHelper.TryParse(script.CronExpression, out var cron) || cron is null)
            {
                _logger.LogWarning("Invalid cron '{Cron}' on script {Script}.", script.CronExpression, script.ScriptId);
                continue;
            }

            var occurrences = cron.GetOccurrences(lastTick.UtcDateTime, now.UtcDateTime, TimeZoneInfo.Utc, fromInclusive: false, toInclusive: true);
            if (!occurrences.Any()) continue;

            try
            {
                _logger.LogInformation("Cron match for {Script}; invoking.", script.Name);
                // triggered_by must be one of the check-constraint tokens
                // ('schedule'|'manual'|'chain'|'event') — the cron itself is on the script row.
                await _engine.RunAsync(script.ScriptId, "schedule", null, null, null, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Scheduled run of {Script} failed.", script.ScriptId);
            }
        }
    }
}
