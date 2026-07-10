namespace RadiologyPlus.Scripting;

// ---------------------------------------------------------------------------
// Admin-facing records for the Script Manager (list/edit/history surfaces).
// The engine keeps using the lean ScriptRecord; these carry the extra columns
// (description, audit timestamps, last-run rollup) the management UI needs.
// ---------------------------------------------------------------------------

public sealed record ScriptSummary(
    Guid ScriptId,
    string Name,
    string? Description,
    ScriptLanguage Language,
    string ConnectionTarget,
    string? CronExpression,
    bool IsActive,
    int TimeoutSeconds,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long? LastExecutionId,
    string? LastStatus,
    DateTimeOffset? LastStartedAt,
    int? LastDurationMs);

public sealed record ScriptDetail(
    Guid ScriptId,
    string Name,
    string? Description,
    ScriptLanguage Language,
    string Body,
    string ConnectionTarget,
    string? CronExpression,
    bool IsActive,
    int TimeoutSeconds,
    IReadOnlyDictionary<string, object?>? Parameters,
    Guid? CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ScriptCreate(
    string Name,
    string? Description,
    ScriptLanguage Language,
    string Body,
    string ConnectionTarget,
    string? CronExpression,
    bool IsActive,
    int TimeoutSeconds,
    IReadOnlyDictionary<string, object?>? Parameters);

public sealed record ScriptUpdate(
    string Name,
    string? Description,
    ScriptLanguage Language,
    string Body,
    string ConnectionTarget,
    string? CronExpression,
    bool IsActive,
    int TimeoutSeconds,
    IReadOnlyDictionary<string, object?>? Parameters);

/// <summary>Before/after pair so the endpoint can audit the transition.</summary>
public sealed record ScriptUpdateResult(ScriptDetail Before, ScriptDetail After);

/// <summary>Version list row — body omitted (fetch a single version for it).</summary>
public sealed record ScriptVersionInfo(
    Guid VersionId,
    Guid ScriptId,
    int VersionNumber,
    int BodyChars,
    Guid? SavedBy,
    DateTimeOffset SavedAt);

public sealed record ScriptVersionDetail(
    Guid VersionId,
    Guid ScriptId,
    int VersionNumber,
    string Body,
    Guid? SavedBy,
    DateTimeOffset SavedAt);

/// <summary>Execution list row — logs omitted (fetch the detail for them).</summary>
public sealed record ScriptExecutionListItem(
    long ExecutionId,
    Guid ScriptId,
    string ScriptName,
    string TriggeredBy,
    string Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int? DurationMs,
    int? RowsAffected,
    DateTimeOffset CreatedAt);

public sealed record ScriptExecutionDetail(
    long ExecutionId,
    Guid ScriptId,
    string ScriptName,
    string TriggeredBy,
    string Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int? DurationMs,
    int? ExitCode,
    string? OutputLog,
    string? ErrorLog,
    int? RowsAffected,
    DateTimeOffset CreatedAt);

/// <summary>
/// Management surface over scripting.scripts / script_versions / executions.
/// Tenant-scoped: every query passes tenant_id explicitly (RLS enforces it too).
/// </summary>
public interface IScriptAdminRepository
{
    Task<IReadOnlyList<ScriptSummary>> ListAllAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task<ScriptDetail?> GetDetailAsync(Guid tenantId, Guid scriptId, CancellationToken cancellationToken = default);
    Task<ScriptDetail> CreateScriptAsync(Guid tenantId, Guid? createdBy, ScriptCreate input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a script; when the body changed, the OLD body is snapshotted into
    /// scripting.script_versions first (next version number, same transaction).
    /// Throws KeyNotFoundException when the script does not exist.
    /// </summary>
    Task<ScriptUpdateResult> UpdateScriptAsync(Guid tenantId, Guid scriptId, Guid? savedBy, ScriptUpdate input, CancellationToken cancellationToken = default);

    /// <summary>Throws KeyNotFoundException when the script does not exist.</summary>
    Task<ScriptDetail> SetScriptActiveAsync(Guid tenantId, Guid scriptId, bool isActive, CancellationToken cancellationToken = default);

    /// <summary>Returns false when no row was removed.</summary>
    Task<bool> DeleteScriptAsync(Guid tenantId, Guid scriptId, CancellationToken cancellationToken = default);

    /// <summary>scriptId null = recent executions across all scripts.</summary>
    Task<IReadOnlyList<ScriptExecutionListItem>> ListExecutionsAsync(Guid tenantId, Guid? scriptId, int limit, CancellationToken cancellationToken = default);
    Task<ScriptExecutionDetail?> GetExecutionAsync(Guid tenantId, long executionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScriptVersionInfo>> ListVersionsAsync(Guid tenantId, Guid scriptId, CancellationToken cancellationToken = default);
    Task<ScriptVersionDetail?> GetVersionAsync(Guid tenantId, Guid versionId, CancellationToken cancellationToken = default);
}
