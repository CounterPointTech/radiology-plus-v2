namespace RadiologyPlus.Core.TechValidation;

/// <summary>
/// Runs the "Do the Do" sequence for a finalized validation.
/// Emits one <see cref="DoTheDoProgressEvent"/> per step so SignalR clients can show the
/// cooking-progress animation.
/// </summary>
public interface IDoTheDoOrchestrator
{
    /// <summary>
    /// Execute the full ordered step sequence. Each step is one transactional INovaradWriter call
    /// and a do_the_do_runs row. If a step throws, subsequent steps are SKIPPED and the validation
    /// is marked Failed.
    /// </summary>
    Task<DoTheDoOutcome> RunAsync(
        Guid validationId,
        IProgress<DoTheDoProgressEvent>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record DoTheDoOutcome(
    Guid ValidationId,
    bool Success,
    int CompletedSteps,
    int TotalSteps,
    string? FailureMessage);

public sealed record DoTheDoProgressEvent(
    Guid ValidationId,
    int StepIndex,
    int StepCount,
    string StepKey,
    string Description,
    DoTheDoRunStatus Status,
    string? ErrorMessage);
