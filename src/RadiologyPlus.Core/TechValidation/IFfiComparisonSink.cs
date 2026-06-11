namespace RadiologyPlus.Core.TechValidation;

/// <summary>
/// Receives the comparison-study IDs the tech flagged on wizard step 3 so they reach
/// the downstream voice-dictation system (M*Modal/FFI) for the radiologist to see.
///
/// Today's persistence sink is <c>pacs.studies.is_study_of_interest</c>, set by the
/// existing <c>pacs.merge_comparisons</c> Do-the-Do step — that part already works.
/// This interface is the stub for the *additional* emission to FFI itself, which is
/// deferred until the integration channel is chosen.
/// </summary>
/// <remarks>
/// Three candidate routes are on the table:
///   TODO(FFI-comparisons) route 1: direct write into FFI's SQL Server tables.
///   TODO(FFI-comparisons) route 2: HL7 ORU/ORM message via existing or new MLLP client.
///   TODO(FFI-comparisons) route 3: crosswalk/staging table that FFI polls.
/// Once decided, the chosen sink replaces <see cref="NoOpFfiComparisonSink"/> in DI
/// and gets called from the orchestrator as one additional Do-the-Do step.
/// </remarks>
public interface IFfiComparisonSink
{
    Task SubmitFlaggedComparisonsAsync(
        Guid validationId,
        IReadOnlyList<long> comparisonStudyIds,
        CancellationToken cancellationToken = default);
}
