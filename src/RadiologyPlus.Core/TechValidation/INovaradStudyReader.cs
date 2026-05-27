namespace RadiologyPlus.Core.TechValidation;

/// <summary>
/// Read-side access into a tenant's Novarad (PACS + RIS). Used by:
///  - <see cref="ITechValidationRepository"/>-callers (orders / comparisons during the wizard)
///  - the <c>ReadyStudiesProjector</c> hosted service (worklist input feed)
/// Writes go through <c>INovaradWriter</c>, not here.
/// </summary>
public interface INovaradStudyReader
{
    /// <summary>Pull all ready-for-tech-validation studies for the current tenant, optionally filtered by facility.</summary>
    /// <param name="window">How far back to look at <c>last_image_processed_date</c>. Default: 7 days.</param>
    Task<IReadOnlyList<ReadyStudy>> ReadReadyStudiesAsync(
        TimeSpan window,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-read a single study (current PACS state, ignoring the ready filter). Used to refresh
    /// the worklist projection right after a patient correction/reassignment so the UI is consistent.
    /// </summary>
    Task<ReadyStudy?> ReadStudyByIdAsync(
        long novaradStudyId,
        CancellationToken cancellationToken = default);

    /// <summary>Search existing patients (PID / name) to pick a reassignment target.</summary>
    Task<IReadOnlyList<PatientSearchResult>> SearchPatientsAsync(
        string query,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Pull candidate orders for the patient on the selected study (ris.orders).</summary>
    Task<IReadOnlyList<CandidateOrder>> ReadCandidateOrdersForPatientAsync(
        string patientPid,
        CancellationToken cancellationToken = default);

    /// <summary>Pull comparison candidates — older studies for the same patient, plus optional fuzzy name match.</summary>
    Task<IReadOnlyList<ComparisonCandidate>> ReadComparisonCandidatesAsync(
        long novaradPatientId,
        string? lastNameFuzzy,
        int limit,
        CancellationToken cancellationToken = default);
}
