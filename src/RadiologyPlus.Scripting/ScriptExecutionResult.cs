namespace RadiologyPlus.Scripting;

public enum ScriptExecutionStatus
{
    Pending,
    Running,
    Success,
    Failed,
    Cancelled,
    Timeout,
}

public sealed class ScriptExecutionResult
{
    public bool Success { get; init; }
    public ScriptExecutionStatus Status { get; init; }
    public string? Message { get; init; }
    public string? Output { get; init; }
    public string? Error { get; init; }
    public long DurationMs { get; init; }
    public int? RowsAffected { get; init; }
    public int? ExitCode { get; init; }
}

public sealed record ScriptInvocation(
    string Body,
    ScriptLanguage Language,
    IReadOnlyDictionary<string, object?>? Parameters,
    string? ConnectionString,
    int TimeoutSeconds,
    int MaxResultRows);

public interface IScriptExecutor
{
    ScriptLanguage Language { get; }

    Task<ScriptExecutionResult> ExecuteAsync(ScriptInvocation invocation, CancellationToken cancellationToken = default);
}
