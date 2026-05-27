using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RadiologyPlus.Scripting.Executors;

public sealed class PowerShellScriptExecutor : IScriptExecutor
{
    private readonly ILogger<PowerShellScriptExecutor> _logger;

    public PowerShellScriptExecutor(ILogger<PowerShellScriptExecutor> logger) => _logger = logger;

    public ScriptLanguage Language => ScriptLanguage.PowerShell;

    public async Task<ScriptExecutionResult> ExecuteAsync(ScriptInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(invocation.Body))
            return Fail("Script body is required.");

        var stopwatch = Stopwatch.StartNew();
        var output = new StringBuilder();
        var error = new StringBuilder();

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(invocation.TimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var sessionState = InitialSessionState.CreateDefault2();
            sessionState.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;

            using var runspace = RunspaceFactory.CreateRunspace(sessionState);
            runspace.Open();

            if (invocation.Parameters is not null)
            {
                foreach (var (k, v) in invocation.Parameters)
                    runspace.SessionStateProxy.SetVariable(k, NormalizeValue(v));
            }

            output.AppendLine(CultureInfo.InvariantCulture, $"PowerShell session started at {DateTimeOffset.UtcNow:O}.");

            using var ps = PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddScript(invocation.Body);

            if (invocation.Parameters is not null)
            {
                foreach (var (k, v) in invocation.Parameters)
                    ps.AddParameter(k, NormalizeValue(v));
            }

            Collection<PSObject>? results = null;
            var invokeTask = Task.Run(() =>
            {
                try { return ps.Invoke(); }
                catch (Exception ex) when (ex is RuntimeException or InvalidOperationException)
                {
                    error.AppendLine(CultureInfo.InvariantCulture, $"EXECUTION ERROR: {ex.Message}");
                    _logger.LogError(ex, "PowerShell invocation failed.");
                    return null;
                }
            }, linkedCts.Token);

            try
            {
                results = await invokeTask.WaitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                ps.Stop();
                throw;
            }

            if (results is not null)
            {
                foreach (var r in results)
                    if (r is not null) output.AppendLine(r.ToString());
            }

            if (ps.HadErrors)
            {
                foreach (var e in ps.Streams.Error)
                    error.AppendLine(CultureInfo.InvariantCulture, $"ERROR: {e}");
            }
            foreach (var w in ps.Streams.Warning) output.AppendLine(CultureInfo.InvariantCulture, $"WARNING: {w}");

            stopwatch.Stop();
            var hasErrors = error.Length > 0 || ps.HadErrors;
            return new ScriptExecutionResult
            {
                Success = !hasErrors,
                Status = hasErrors ? ScriptExecutionStatus.Failed : ScriptExecutionStatus.Success,
                Message = hasErrors ? "Script completed with errors" : "OK",
                Output = Truncate(output.ToString()),
                Error = hasErrors ? Truncate(error.ToString()) : null,
                DurationMs = stopwatch.ElapsedMilliseconds,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new ScriptExecutionResult
            {
                Success = false,
                Status = ScriptExecutionStatus.Cancelled,
                Message = "Cancelled",
                DurationMs = stopwatch.ElapsedMilliseconds,
            };
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new ScriptExecutionResult
            {
                Success = false,
                Status = ScriptExecutionStatus.Timeout,
                Message = $"Timed out after {invocation.TimeoutSeconds}s",
                DurationMs = stopwatch.ElapsedMilliseconds,
            };
        }
        catch (Exception ex) when (ex is RuntimeException or InvalidOperationException)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "PowerShell script execution failed.");
            return new ScriptExecutionResult
            {
                Success = false,
                Status = ScriptExecutionStatus.Failed,
                Message = ex.Message,
                Error = ex.ToString(),
                DurationMs = stopwatch.ElapsedMilliseconds,
            };
        }
    }

    private static object? NormalizeValue(object? value)
    {
        if (value is JsonElement el)
        {
            return el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => el.ToString(),
            };
        }
        return value;
    }

    private static ScriptExecutionResult Fail(string reason) => new()
    {
        Success = false,
        Status = ScriptExecutionStatus.Failed,
        Message = reason,
        Error = reason,
    };

    private static string? Truncate(string s, int max = 50_000) =>
        s.Length <= max ? s : s[..max] + $"\n... ({s.Length - max} more chars)";
}
