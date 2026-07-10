using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace RadiologyPlus.Scripting;

/// <summary>
/// Runs scripts: persists execution records, dispatches to the right executor, tracks
/// concurrency. Chains + cross-script notification orchestration are deferred to Phase 4
/// (when the NRS UI lands) — this is intentionally a smaller surface than the v2.0 prototype.
/// </summary>
public sealed class ScriptExecutionEngine : IDisposable
{
    public void Dispose() => _semaphore.Dispose();

    private readonly ScriptExecutorFactory _executors;
    private readonly IScriptRepository _scripts;
    private readonly IScriptConnectionResolver _connections;
    private readonly ILogger<ScriptExecutionEngine> _logger;
    private readonly SemaphoreSlim _semaphore;
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _running = new();

    public ScriptExecutionEngine(
        ScriptExecutorFactory executors,
        IScriptRepository scripts,
        IScriptConnectionResolver connections,
        ILogger<ScriptExecutionEngine> logger,
        int maxConcurrent = 5)
    {
        _executors = executors;
        _scripts = scripts;
        _connections = connections;
        _logger = logger;
        _semaphore = new SemaphoreSlim(Math.Max(1, maxConcurrent));
    }

    public async Task<ScriptExecutionRecord> RunAsync(
        Guid scriptId,
        string triggeredBy,
        Guid? triggeredByUser = null,
        IReadOnlyDictionary<string, object?>? parameterOverrides = null,
        string? connectionString = null,
        CancellationToken cancellationToken = default)
    {
        var script = await _scripts.GetAsync(scriptId, cancellationToken)
            ?? throw new InvalidOperationException($"Script {scriptId} not found.");

        if (!script.IsActive)
            throw new InvalidOperationException($"Script {scriptId} is not active.");

        var merged = MergeParameters(script.Parameters, parameterOverrides);
        var executionId = await _scripts.CreateExecutionAsync(scriptId, script.TenantId, triggeredBy, triggeredByUser, merged, cancellationToken);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _running[executionId] = cts;

        await _semaphore.WaitAsync(cts.Token);
        try
        {
            await _scripts.MarkRunningAsync(executionId, cts.Token);
            _logger.LogInformation("Executing script {Script} (execution {Execution}) for tenant {Tenant}.",
                script.Name, executionId, script.TenantId);

            // No explicit override -> resolve from the script's connection_target
            // (appdb | novarad | mmodal | none). A missing tenant connection is a
            // clean failed execution, not a stuck 'running' row.
            var effectiveConnection = connectionString;
            if (effectiveConnection is null)
            {
                try
                {
                    effectiveConnection = await _connections.ResolveAsync(script.TenantId, script.ConnectionTarget, cts.Token);
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogWarning(ex, "Connection resolution failed for script {Script} (execution {Execution}).",
                        script.ScriptId, executionId);
                    var failed = new ScriptExecutionResult
                    {
                        Success = false,
                        Status = ScriptExecutionStatus.Failed,
                        Message = ex.Message,
                        Error = ex.Message,
                        DurationMs = 0,
                    };
                    await _scripts.UpdateExecutionAsync(executionId, failed, cts.Token);
                    return new ScriptExecutionRecord(
                        executionId, script.ScriptId, script.TenantId, triggeredBy, triggeredByUser,
                        failed.Status, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                        0, null, null, failed.Error, null, merged);
                }
            }

            var executor = _executors.Get(script.Language);
            var invocation = new ScriptInvocation(
                Body: script.Body,
                Language: script.Language,
                Parameters: merged,
                ConnectionString: effectiveConnection,
                TimeoutSeconds: script.TimeoutSeconds,
                MaxResultRows: 100);

            var result = await executor.ExecuteAsync(invocation, cts.Token);
            await _scripts.UpdateExecutionAsync(executionId, result, cts.Token);

            return new ScriptExecutionRecord(
                executionId, script.ScriptId, script.TenantId, triggeredBy, triggeredByUser,
                result.Status, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                (int)result.DurationMs, result.ExitCode, result.Output, result.Error,
                result.RowsAffected, merged);
        }
        finally
        {
            _semaphore.Release();
            _running.TryRemove(executionId, out _);
            cts.Dispose();
        }
    }

    public bool TryCancel(long executionId)
    {
        if (_running.TryGetValue(executionId, out var cts))
        {
            cts.Cancel();
            return true;
        }
        return false;
    }

    private static Dictionary<string, object?>? MergeParameters(
        IReadOnlyDictionary<string, object?>? defaults,
        IReadOnlyDictionary<string, object?>? overrides)
    {
        if (defaults is null && overrides is null) return null;
        var merged = new Dictionary<string, object?>();
        if (defaults is not null) foreach (var (k, v) in defaults) merged[k] = v;
        if (overrides is not null) foreach (var (k, v) in overrides) merged[k] = v;
        return merged;
    }
}
