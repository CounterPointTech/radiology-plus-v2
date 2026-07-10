namespace RadiologyPlus.Scripting;

public sealed record ScriptRecord(
    Guid ScriptId,
    Guid TenantId,
    string Name,
    ScriptLanguage Language,
    string Body,
    string? CronExpression,
    bool IsActive,
    int TimeoutSeconds,
    IReadOnlyDictionary<string, object?>? Parameters,
    string ConnectionTarget);

public sealed record ScriptExecutionRecord(
    long ExecutionId,
    Guid ScriptId,
    Guid TenantId,
    string TriggeredBy,
    Guid? TriggeredByUser,
    ScriptExecutionStatus Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int? DurationMs,
    int? ExitCode,
    string? OutputLog,
    string? ErrorLog,
    int? RowsAffected,
    IReadOnlyDictionary<string, object?>? ParametersUsed);

public interface IScriptRepository
{
    Task<ScriptRecord?> GetAsync(Guid scriptId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScriptRecord>> ListActiveAsync(CancellationToken cancellationToken = default);
    Task<long> CreateExecutionAsync(Guid scriptId, Guid tenantId, string triggeredBy, Guid? userId, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken = default);
    Task UpdateExecutionAsync(long executionId, ScriptExecutionResult result, CancellationToken cancellationToken = default);
    Task MarkRunningAsync(long executionId, CancellationToken cancellationToken = default);
}
