namespace RadiologyPlus.Scripting;

// ---------------------------------------------------------------------------
// Script chains: run several scripts in order as one unit. The runner-side
// records here stay lean (like ScriptRecord); the admin records carry the
// extra columns the Chains console needs. on_failure is 'stop' | 'continue'
// ('branch' exists in the 0001 CHECK but has no target columns — unsupported).
// ---------------------------------------------------------------------------

/// <summary>Runner-side chain row with its ordered links.</summary>
public sealed record ChainRecord(
    Guid ChainId,
    Guid TenantId,
    string Name,
    string OnFailure,
    string? CronExpression,
    bool IsActive,
    string? NotifyOnFailureRecipient,
    Guid? NotifyOnFailureTemplateId,
    IReadOnlyList<ChainLinkRecord> Links);

public sealed record ChainLinkRecord(
    int StepOrder,
    Guid ScriptId,
    bool ContinueOnFailure);

/// <summary>
/// Runner-side persistence for chains and chain runs. Unscoped (the runner and
/// scheduler work across tenants, like IScriptRepository).
/// </summary>
public interface IScriptChainRepository
{
    Task<ChainRecord?> GetAsync(Guid chainId, CancellationToken cancellationToken = default);

    /// <summary>Active chains only — the scheduler filters for a cron itself.</summary>
    Task<IReadOnlyList<ChainRecord>> ListActiveAsync(CancellationToken cancellationToken = default);

    Task<long> CreateChainRunAsync(Guid chainId, Guid tenantId, string triggeredBy, Guid? userId, int stepsTotal, CancellationToken cancellationToken = default);
    Task MarkChainRunRunningAsync(long chainRunId, CancellationToken cancellationToken = default);
    Task CompleteChainRunAsync(long chainRunId, string status, int stepsSucceeded, int stepsFailed, string? errorSummary, CancellationToken cancellationToken = default);
}

/// <summary>
/// Failure-notification hook: implemented in the Data layer on top of the
/// notifications queue so the Scripting project stays decoupled from it.
/// Implementations must never throw — a broken notification can't fail a run.
/// </summary>
public interface IChainFailureNotifier
{
    Task NotifyAsync(ChainFailureContext context, CancellationToken cancellationToken = default);
}

public sealed record ChainFailureContext(
    Guid TenantId,
    Guid ChainId,
    string ChainName,
    long ChainRunId,
    string Recipient,
    Guid? TemplateId,
    int StepsTotal,
    int StepsSucceeded,
    int StepsFailed,
    string? ErrorSummary);

// ---------------------------------------------------------------------------
// Admin surface
// ---------------------------------------------------------------------------

public sealed record ChainSummary(
    Guid ChainId,
    string Name,
    string? Description,
    string OnFailure,
    string? CronExpression,
    bool IsActive,
    int StepCount,
    bool NotifiesOnFailure,
    DateTimeOffset CreatedAt,
    long? LastRunId,
    string? LastRunStatus,
    DateTimeOffset? LastRunStartedAt,
    int? LastRunDurationMs);

public sealed record ChainStepInfo(
    int StepOrder,
    Guid ScriptId,
    string ScriptName,
    ScriptLanguage Language,
    bool ScriptIsActive,
    bool ContinueOnFailure);

public sealed record ChainDetail(
    Guid ChainId,
    string Name,
    string? Description,
    string OnFailure,
    string? CronExpression,
    bool IsActive,
    string? NotifyOnFailureRecipient,
    Guid? NotifyOnFailureTemplateId,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ChainStepInfo> Steps);

public sealed record ChainStepSave(
    Guid ScriptId,
    bool ContinueOnFailure);

public sealed record ChainSave(
    string Name,
    string? Description,
    string OnFailure,
    string? CronExpression,
    bool IsActive,
    string? NotifyOnFailureRecipient,
    Guid? NotifyOnFailureTemplateId,
    IReadOnlyList<ChainStepSave> Steps);

public sealed record ChainRunInfo(
    long ChainRunId,
    Guid ChainId,
    string ChainName,
    string TriggeredBy,
    string Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int? DurationMs,
    int StepsTotal,
    int StepsSucceeded,
    int StepsFailed,
    string? ErrorSummary,
    DateTimeOffset CreatedAt);

/// <summary>A step execution inside a run (a scripting.executions row, logs omitted).</summary>
public sealed record ChainRunStep(
    long ExecutionId,
    Guid ScriptId,
    string ScriptName,
    string Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int? DurationMs,
    int? RowsAffected);

public sealed record ChainRunDetail(
    ChainRunInfo Run,
    IReadOnlyList<ChainRunStep> Steps);

/// <summary>
/// Management surface over scripting.script_chains / script_chain_links / chain_runs.
/// Tenant-scoped: every query passes tenant_id explicitly (RLS enforces it too).
/// </summary>
public interface IChainAdminRepository
{
    Task<IReadOnlyList<ChainSummary>> ListAllAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task<ChainDetail?> GetDetailAsync(Guid tenantId, Guid chainId, CancellationToken cancellationToken = default);

    /// <summary>Throws InvalidOperationException when a step's script doesn't exist for this tenant.</summary>
    Task<ChainDetail> CreateAsync(Guid tenantId, ChainSave input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the chain row and its links in one transaction.
    /// Throws KeyNotFoundException when the chain does not exist,
    /// InvalidOperationException when a step's script doesn't exist for this tenant.
    /// </summary>
    Task<ChainDetail> UpdateAsync(Guid tenantId, Guid chainId, ChainSave input, CancellationToken cancellationToken = default);

    /// <summary>Throws KeyNotFoundException when the chain does not exist.</summary>
    Task<ChainDetail> SetActiveAsync(Guid tenantId, Guid chainId, bool isActive, CancellationToken cancellationToken = default);

    /// <summary>Returns false when no row was removed.</summary>
    Task<bool> DeleteAsync(Guid tenantId, Guid chainId, CancellationToken cancellationToken = default);

    /// <summary>chainId null = recent runs across all chains.</summary>
    Task<IReadOnlyList<ChainRunInfo>> ListRunsAsync(Guid tenantId, Guid? chainId, int limit, CancellationToken cancellationToken = default);
    Task<ChainRunDetail?> GetRunAsync(Guid tenantId, long chainRunId, CancellationToken cancellationToken = default);
}
