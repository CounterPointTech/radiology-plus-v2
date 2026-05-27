using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace RadiologyPlus.Scripting.Executors;

public sealed class PostgresScriptExecutor : IScriptExecutor
{
    private readonly ILogger<PostgresScriptExecutor> _logger;

    public PostgresScriptExecutor(ILogger<PostgresScriptExecutor> logger) => _logger = logger;

    public ScriptLanguage Language => ScriptLanguage.Pgsql;

    public async Task<ScriptExecutionResult> ExecuteAsync(ScriptInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(invocation.Body))
            return Fail("Script body is required.");
        if (string.IsNullOrWhiteSpace(invocation.ConnectionString))
            return Fail("Connection string is required for PostgreSQL scripts.");

        var stopwatch = Stopwatch.StartNew();
        var output = new StringBuilder();
        var error = new StringBuilder();
        var totalRows = 0;

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(invocation.TimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            await using var conn = new NpgsqlConnection(invocation.ConnectionString);
            await conn.OpenAsync(linkedCts.Token);
            output.AppendLine(CultureInfo.InvariantCulture, $"Connected to PostgreSQL at {DateTimeOffset.UtcNow:O}");

            var processed = ParameterSubstitution.SubstituteSqlStyle(invocation.Body, invocation.Parameters);
            var statements = SqlStatementSplitter.SplitPostgres(processed);
            output.AppendLine(CultureInfo.InvariantCulture, $"{statements.Count} statement(s) to execute.");

            foreach (var (idx, stmt) in statements.Select((s, i) => (i + 1, s)))
            {
                linkedCts.Token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(stmt)) continue;

                try
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = stmt;
                    cmd.CommandTimeout = invocation.TimeoutSeconds;

                    if (IsSelectLike(stmt))
                    {
                        await using var reader = await cmd.ExecuteReaderAsync(linkedCts.Token);
                        var rowCount = 0;
                        while (await reader.ReadAsync(linkedCts.Token) && rowCount < invocation.MaxResultRows)
                        {
                            rowCount++;
                        }
                        totalRows += rowCount;
                        output.AppendLine(CultureInfo.InvariantCulture, $"  [{idx}] SELECT — {rowCount} row(s)");
                    }
                    else
                    {
                        var affected = await cmd.ExecuteNonQueryAsync(linkedCts.Token);
                        totalRows += Math.Max(affected, 0);
                        output.AppendLine(CultureInfo.InvariantCulture, $"  [{idx}] {affected} row(s) affected");
                    }
                }
                catch (NpgsqlException ex)
                {
                    error.AppendLine(CultureInfo.InvariantCulture, $"Statement {idx} failed: {ex.Message}");
                    _logger.LogError(ex, "Postgres statement {Index} failed.", idx);
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
                Output = Truncate(output.ToString()),
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
                Output = Truncate(output.ToString()),
                DurationMs = stopwatch.ElapsedMilliseconds,
            };
        }
        catch (NpgsqlException ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Postgres script execution failed.");
            return new ScriptExecutionResult
            {
                Success = false,
                Status = ScriptExecutionStatus.Failed,
                Message = ex.Message,
                Output = Truncate(output.ToString()),
                Error = ex.ToString(),
                DurationMs = stopwatch.ElapsedMilliseconds,
            };
        }
    }

    private static bool IsSelectLike(string sql)
    {
        var trimmed = sql.TrimStart();
        return trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("EXPLAIN", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("SHOW", StringComparison.OrdinalIgnoreCase);
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
