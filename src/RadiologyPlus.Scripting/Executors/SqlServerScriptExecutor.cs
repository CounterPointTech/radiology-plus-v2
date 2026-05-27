using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace RadiologyPlus.Scripting.Executors;

public sealed partial class SqlServerScriptExecutor : IScriptExecutor
{
    private readonly ILogger<SqlServerScriptExecutor> _logger;

    public SqlServerScriptExecutor(ILogger<SqlServerScriptExecutor> logger) => _logger = logger;

    public ScriptLanguage Language => ScriptLanguage.Tsql;

    public async Task<ScriptExecutionResult> ExecuteAsync(ScriptInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(invocation.Body))
            return Fail("Script body is required.");
        if (string.IsNullOrWhiteSpace(invocation.ConnectionString))
            return Fail("Connection string is required for T-SQL scripts.");

        var stopwatch = Stopwatch.StartNew();
        var output = new StringBuilder();
        var error = new StringBuilder();
        var totalRows = 0;

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(invocation.TimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            await using var conn = new SqlConnection(invocation.ConnectionString);
            await conn.OpenAsync(linkedCts.Token);
            output.AppendLine(CultureInfo.InvariantCulture, $"Connected to SQL Server at {DateTimeOffset.UtcNow:O}");

            var processed = ParameterSubstitution.SubstituteSqlStyle(invocation.Body, invocation.Parameters);
            var batches = SplitByGo(processed);
            output.AppendLine(CultureInfo.InvariantCulture, $"{batches.Count} batch(es) to execute.");

            for (var i = 0; i < batches.Count; i++)
            {
                var batch = batches[i];
                if (string.IsNullOrWhiteSpace(batch)) continue;

                linkedCts.Token.ThrowIfCancellationRequested();

                try
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = batch;
                    cmd.CommandTimeout = invocation.TimeoutSeconds;
                    var affected = await cmd.ExecuteNonQueryAsync(linkedCts.Token);
                    if (affected >= 0) totalRows += affected;
                    output.AppendLine(CultureInfo.InvariantCulture, $"  [{i + 1}] affected {affected}");
                }
                catch (SqlException ex)
                {
                    error.AppendLine(CultureInfo.InvariantCulture, $"Batch {i + 1} failed: {ex.Message}");
                    _logger.LogError(ex, "T-SQL batch {Index} failed.", i + 1);
                }
            }

            stopwatch.Stop();
            return new ScriptExecutionResult
            {
                Success = error.Length == 0,
                Status = error.Length == 0 ? ScriptExecutionStatus.Success : ScriptExecutionStatus.Failed,
                Message = error.Length == 0 ? "OK" : "Script completed with errors",
                Output = Truncate(output.ToString()),
                Error = error.Length == 0 ? null : Truncate(error.ToString()),
                DurationMs = stopwatch.ElapsedMilliseconds,
                RowsAffected = totalRows,
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
        catch (SqlException ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "T-SQL script execution failed.");
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

    private static List<string> SplitByGo(string script)
    {
        return GoSeparator()
            .Split(script)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToList();
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();

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
