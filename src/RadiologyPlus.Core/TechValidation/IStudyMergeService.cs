namespace RadiologyPlus.Core.TechValidation;

/// <summary>
/// DICOM study-level merge: re-point every <c>pacs.series.study</c> for each
/// losing study onto the keeper, then soft-delete the losing <c>pacs.studies</c>
/// row. Each losing study is its own dual-audited Novarad transaction —
/// per-study atomicity, run sequentially. Pre-flight validation ensures all
/// studies belong to the same <c>pacs.patients</c> row and are all in NEW
/// status (status=0) with is_valid=TRUE.
/// </summary>
public interface IStudyMergeService
{
    Task<StudyMergeOutcome> MergeStudiesAsync(
        StudyMergeRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Inputs to <see cref="IStudyMergeService.MergeStudiesAsync"/>.</summary>
/// <param name="WinningStudyId">The study that survives the merge ("keeper").</param>
/// <param name="LosingStudyIds">Studies whose series get re-pointed onto the keeper, then soft-deleted.</param>
/// <param name="Reason">Optional free-text reason captured on each audit row.</param>
public sealed record StudyMergeRequest(
    long WinningStudyId,
    IReadOnlyList<long> LosingStudyIds,
    string? Reason);

/// <summary>Aggregate outcome of a merge attempt.</summary>
public sealed record StudyMergeOutcome(
    long WinningStudyId,
    int MergedCount,
    int FailedCount,
    IReadOnlyList<StudyMergeRowResult> Rows,
    string? PreflightError);

/// <summary>One row per losing study in <see cref="StudyMergeOutcome.Rows"/>.</summary>
public sealed record StudyMergeRowResult(
    long LosingStudyId,
    bool Success,
    int SeriesRePointed,
    string? ErrorMessage);
