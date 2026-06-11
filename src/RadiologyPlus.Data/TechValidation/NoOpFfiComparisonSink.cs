using Microsoft.Extensions.Logging;
using RadiologyPlus.Core.TechValidation;

namespace RadiologyPlus.Data.TechValidation;

/// <summary>
/// Default <see cref="IFfiComparisonSink"/> implementation. Logs the call and returns —
/// the actual FFI write happens here once the integration route is chosen (see the
/// <c>TODO(FFI-comparisons)</c> markers on the interface). Until then, the persisted
/// <c>validations.comparison_study_ids</c> column + the existing <c>pacs.merge_comparisons</c>
/// Do-the-Do step (which flips <c>is_study_of_interest</c>) carry the same intent.
/// </summary>
public sealed class NoOpFfiComparisonSink : IFfiComparisonSink
{
    private readonly ILogger<NoOpFfiComparisonSink> _logger;

    public NoOpFfiComparisonSink(ILogger<NoOpFfiComparisonSink> logger) => _logger = logger;

    public Task SubmitFlaggedComparisonsAsync(
        Guid validationId,
        IReadOnlyList<long> comparisonStudyIds,
        CancellationToken cancellationToken = default)
    {
        // TODO(FFI-comparisons): emit to the chosen route once decided. Until then,
        // this is a no-op so the orchestrator can wire the call without behavior change.
        _logger.LogInformation(
            "NoOpFfiComparisonSink: would emit {Count} comparison study id(s) for validation {Validation}.",
            comparisonStudyIds.Count, validationId);
        return Task.CompletedTask;
    }
}
