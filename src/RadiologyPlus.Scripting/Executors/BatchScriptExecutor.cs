using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RadiologyPlus.Scripting.Executors;

public sealed class BatchScriptExecutor : IScriptExecutor
{
    private readonly ILogger<BatchScriptExecutor> _logger;

    public BatchScriptExecutor(ILogger<BatchScriptExecutor> logger) => _logger = logger;

    public ScriptLanguage Language => ScriptLanguage.Batch;

    public async Task<ScriptExecutionResult> ExecuteAsync(ScriptInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(invocation.Body))
            return Fail("Script body is required.");

        var stopwatch = Stopwatch.StartNew();
        var output = new StringBuilder();
        var error = new StringBuilder();
        string? temp = null;

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(invocation.TimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            temp = Path.Combine(Path.GetTempPath(), $"radplus_{Guid.NewGuid():N}.bat");
            var wrapped = WrapWithParameters(invocation.Body, invocation.Parameters);
            await File.WriteAllTextAsync(temp, wrapped, linkedCts.Token);

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{temp}\"\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath(),
            };
            if (invocation.Parameters is not null)
            {
                foreach (var (k, v) in invocation.Parameters)
                    psi.EnvironmentVariables[$"RADPLUS_{k.ToUpperInvariant()}"] = v?.ToString() ?? "";
            }

            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) error.AppendLine(e.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }

            stopwatch.Stop();
            output.AppendLine(CultureInfo.InvariantCulture, $"Exit code: {process.ExitCode}, duration: {stopwatch.ElapsedMilliseconds}ms");
            var hasErrors = process.ExitCode != 0 || error.Length > 0;
            return new ScriptExecutionResult
            {
                Success = !hasErrors,
                Status = hasErrors ? ScriptExecutionStatus.Failed : ScriptExecutionStatus.Success,
                Message = hasErrors ? $"Exit {process.ExitCode}" : "OK",
                Output = Truncate(output.ToString()),
                Error = hasErrors ? Truncate(error.ToString()) : null,
                ExitCode = process.ExitCode,
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
        catch (IOException ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Batch script execution failed.");
            return new ScriptExecutionResult
            {
                Success = false,
                Status = ScriptExecutionStatus.Failed,
                Message = ex.Message,
                Error = ex.ToString(),
                DurationMs = stopwatch.ElapsedMilliseconds,
            };
        }
        finally
        {
            if (temp is not null && File.Exists(temp))
            {
                try { File.Delete(temp); }
                catch (IOException ex) { _logger.LogWarning(ex, "Could not delete temp batch file {Path}.", temp); }
            }
        }
    }

    private static string WrapWithParameters(string body, IReadOnlyDictionary<string, object?>? parameters)
    {
        if (parameters is null || parameters.Count == 0) return $"@echo off\r\n{body}";

        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("REM Set parameters from RadiologyPlus");
        foreach (var (k, v) in parameters)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"SET {k}={v?.ToString() ?? ""}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"SET RADPLUS_{k.ToUpperInvariant()}={v?.ToString() ?? ""}");
        }
        sb.AppendLine();

        var processed = body;
        foreach (var (k, _) in parameters)
        {
            processed = processed.Replace("{{" + k + "}}", "%" + k + "%", StringComparison.OrdinalIgnoreCase);
        }
        sb.Append(processed);
        return sb.ToString();
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
