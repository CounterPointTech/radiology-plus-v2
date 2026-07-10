using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace RadiologyPlus.Scripting;

/// <summary>
/// Runs script chains: each step goes through the real ScriptExecutionEngine
/// (triggered_by='chain', linked via chain_run_id), in step order.
///
/// Failure semantics: a failed step marked continue_on_failure is tolerated —
/// it never fails the chain. Any other failure fails the chain; when on_failure
/// is 'stop' it also aborts the remaining steps, 'continue' keeps going.
/// ('branch' has no schema support and is rejected by the admin surface.)
///
/// <see cref="StartAsync"/> creates the run row synchronously (so callers get
/// the id to poll) and executes the steps on a background task — a chain can
/// run for hours; nobody holds an HTTP request for it.
/// </summary>
public sealed class ChainRunner : IDisposable
{
    private readonly ScriptExecutionEngine _engine;
    private readonly IScriptChainRepository _chains;
    private readonly IChainFailureNotifier _notifier;
    private readonly ILogger<ChainRunner> _logger;
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _running = new();

    public ChainRunner(
        ScriptExecutionEngine engine,
        IScriptChainRepository chains,
        IChainFailureNotifier notifier,
        ILogger<ChainRunner> logger)
    {
        _engine = engine;
        _chains = chains;
        _notifier = notifier;
        _logger = logger;
    }

    public void Dispose()
    {
        foreach (var cts in _running.Values) cts.Dispose();
    }

    /// <summary>
    /// Validates the chain, creates the pending run row, and kicks off execution
    /// in the background. Throws InvalidOperationException when the chain is
    /// missing, inactive, or empty.
    /// </summary>
    public async Task<long> StartAsync(Guid chainId, string triggeredBy, Guid? userId, CancellationToken cancellationToken = default)
    {
        var chain = await _chains.GetAsync(chainId, cancellationToken)
            ?? throw new InvalidOperationException($"Chain {chainId} not found.");
        if (!chain.IsActive)
            throw new InvalidOperationException($"Chain {chainId} is not active.");
        if (chain.Links.Count == 0)
            throw new InvalidOperationException($"Chain {chainId} has no steps.");

        var runId = await _chains.CreateChainRunAsync(
            chainId, chain.TenantId, triggeredBy, userId, chain.Links.Count, cancellationToken);

        // Deliberately NOT the caller's token — the run must survive the HTTP response
        // (manual runs) or the scheduler tick (scheduled runs). Cancellation goes
        // through TryCancel instead.
        var cts = new CancellationTokenSource();
        _running[runId] = cts;
        _ = Task.Run(() => ExecuteRunAsync(chain, runId, userId, cts), CancellationToken.None);

        return runId;
    }

    /// <summary>Cancels a run in THIS host's runner: stops the current step, skips the rest.</summary>
    public bool TryCancel(long chainRunId)
    {
        if (_running.TryGetValue(chainRunId, out var cts))
        {
            cts.Cancel();
            return true;
        }
        return false;
    }

    private async Task ExecuteRunAsync(ChainRecord chain, long runId, Guid? userId, CancellationTokenSource cts)
    {
        var succeeded = 0;
        var failed = 0;
        var errors = new List<string>();
        var chainFailed = false;
        string finalStatus;

        try
        {
            await _chains.MarkChainRunRunningAsync(runId, cts.Token);
            _logger.LogInformation("Chain '{Chain}' run {Run} started ({Steps} steps, on_failure={Policy}).",
                chain.Name, runId, chain.Links.Count, chain.OnFailure);

            foreach (var link in chain.Links.OrderBy(l => l.StepOrder))
            {
                cts.Token.ThrowIfCancellationRequested();

                var stepOk = false;
                string? stepError = null;
                try
                {
                    var record = await _engine.RunAsync(
                        link.ScriptId, "chain", userId,
                        chainRunId: runId, cancellationToken: cts.Token);
                    stepOk = record.Status == ScriptExecutionStatus.Success;
                    if (!stepOk) stepError = record.ErrorLog ?? $"script ended with status {record.Status}";
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (InvalidOperationException ex)
                {
                    // Script missing or inactive — the engine throws before creating an
                    // execution row, so the only trace is the chain run's error summary.
                    stepError = ex.Message;
                }

                // A cancel lands as a Cancelled step RESULT (the executor handles the
                // token itself) — that's a cancelled run, not a failed step.
                if (cts.Token.IsCancellationRequested)
                {
                    errors.Add("Run cancelled; remaining steps skipped.");
                    break;
                }

                if (stepOk)
                {
                    succeeded++;
                    continue;
                }

                failed++;
                errors.Add($"Step {link.StepOrder}: {stepError ?? "failed"}");
                if (link.ContinueOnFailure)
                {
                    _logger.LogInformation("Chain run {Run} step {Step} failed but is marked okay-to-fail; continuing.",
                        runId, link.StepOrder);
                    continue;
                }

                chainFailed = true;
                if (chain.OnFailure == "stop")
                {
                    errors.Add("Remaining steps skipped (on-failure policy: stop).");
                    break;
                }
            }

            finalStatus = cts.Token.IsCancellationRequested
                ? "cancelled"
                : chainFailed ? "failed" : "success";
        }
        catch (OperationCanceledException)
        {
            errors.Add("Run cancelled; remaining steps skipped.");
            finalStatus = "cancelled";
        }
        // Boundary guard: this runs on an unobserved background task — anything
        // escaping here would be lost, and the run row stuck at 'running'.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Chain run {Run} crashed.", runId);
            errors.Add(ex.Message);
            finalStatus = "failed";
        }
        finally
        {
            _running.TryRemove(runId, out _);
            cts.Dispose();
        }

        var summary = errors.Count > 0 ? string.Join("\n", errors) : null;
        try
        {
            await _chains.CompleteChainRunAsync(runId, finalStatus, succeeded, failed, summary, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Failed to persist final state of chain run {Run} ({Status}).", runId, finalStatus);
        }
        _logger.LogInformation("Chain '{Chain}' run {Run} finished: {Status} ({Ok} ok / {Failed} failed).",
            chain.Name, runId, finalStatus, succeeded, failed);

        if (finalStatus == "failed" && !string.IsNullOrWhiteSpace(chain.NotifyOnFailureRecipient))
        {
            // The notifier contract is fire-and-log — but belt and braces here:
            // a notification problem must never surface as a runner crash.
            try
            {
                await _notifier.NotifyAsync(new ChainFailureContext(
                    chain.TenantId, chain.ChainId, chain.Name, runId,
                    chain.NotifyOnFailureRecipient!, chain.NotifyOnFailureTemplateId,
                    chain.Links.Count, succeeded, failed, summary), CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogError(ex, "Failure notification for chain run {Run} could not be queued.", runId);
            }
        }
    }
}
