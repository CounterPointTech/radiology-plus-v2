using Cronos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RadiologyPlus.Scripting;

/// <summary>
/// Background service that evaluates active scripts AND active script chains every
/// minute. If a cron expression matches the current minute and it hasn't been run
/// for this slot yet, scripts are invoked via the ScriptExecutionEngine and chains
/// via the ChainRunner (which executes on its own background task — a long chain
/// must not stall the tick).
/// </summary>
public sealed class ScriptScheduler : BackgroundService
{
    private readonly IScriptRepository _scripts;
    private readonly IScriptChainRepository _chains;
    private readonly ScriptExecutionEngine _engine;
    private readonly ChainRunner _chainRunner;
    private readonly ILogger<ScriptScheduler> _logger;
    private DateTimeOffset _lastTick = DateTimeOffset.MinValue;

    public ScriptScheduler(
        IScriptRepository scripts,
        IScriptChainRepository chains,
        ScriptExecutionEngine engine,
        ChainRunner chainRunner,
        ILogger<ScriptScheduler> logger)
    {
        _scripts = scripts;
        _chains = chains;
        _engine = engine;
        _chainRunner = chainRunner;
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
            if (!CronMatched(script.CronExpression, lastTick, now, "script", script.ScriptId)) continue;

            try
            {
                _logger.LogInformation("Cron match for {Script}; invoking.", script.Name);
                // triggered_by must be one of the check-constraint tokens
                // ('schedule'|'manual'|'chain'|'event') — the cron itself is on the script row.
                await _engine.RunAsync(script.ScriptId, "schedule", null, null, null, null, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Scheduled run of {Script} failed.", script.ScriptId);
            }
        }

        var chains = await _chains.ListActiveAsync(cancellationToken);
        foreach (var chain in chains)
        {
            if (!CronMatched(chain.CronExpression, lastTick, now, "chain", chain.ChainId)) continue;

            try
            {
                _logger.LogInformation("Cron match for chain {Chain}; starting run.", chain.Name);
                // StartAsync only creates the run row; the steps execute on the
                // runner's own background task, so the tick isn't held up.
                await _chainRunner.StartAsync(chain.ChainId, "schedule", null, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Scheduled run of chain {Chain} failed to start.", chain.ChainId);
            }
        }
    }

    private bool CronMatched(string? expression, DateTimeOffset lastTick, DateTimeOffset now, string kind, Guid id)
    {
        if (string.IsNullOrWhiteSpace(expression)) return false;
        if (!CronHelper.TryParse(expression, out var cron) || cron is null)
        {
            _logger.LogWarning("Invalid cron '{Cron}' on {Kind} {Id}.", expression, kind, id);
            return false;
        }
        return cron
            .GetOccurrences(lastTick.UtcDateTime, now.UtcDateTime, TimeZoneInfo.Utc, fromInclusive: false, toInclusive: true)
            .Any();
    }
}
