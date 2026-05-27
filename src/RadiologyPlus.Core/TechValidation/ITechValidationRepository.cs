namespace RadiologyPlus.Core.TechValidation;

public interface ITechValidationRepository
{
    // Worklist
    Task<IReadOnlyList<ReadyStudy>> GetWorklistAsync(
        Guid tenantId,
        IReadOnlyCollection<int>? facilityIdFilter,
        int limit,
        CancellationToken cancellationToken = default);

    // Wizard lifecycle
    Task<Guid> StartValidationAsync(
        Guid tenantId,
        long novaradStudyId,
        long novaradPatientId,
        Guid startedByUserId,
        CancellationToken cancellationToken = default);

    Task<ValidationRecord?> GetValidationAsync(
        Guid tenantId, Guid validationId, CancellationToken cancellationToken = default);

    Task<ValidationRecord?> GetOpenValidationForStudyAsync(
        Guid tenantId, long novaradStudyId, CancellationToken cancellationToken = default);

    Task UpdateStepAsync(
        Guid tenantId,
        Guid validationId,
        WizardStep wizardStep,
        ValidationStepUpdate update,
        CancellationToken cancellationToken = default);

    Task SetStatusAsync(
        Guid tenantId, Guid validationId, ValidationStatus status,
        CancellationToken cancellationToken = default);

    Task MarkCompletedAsync(
        Guid tenantId, Guid validationId, CancellationToken cancellationToken = default);

    // Do-the-Do step audit
    Task<long> RecordRunStartedAsync(
        Guid tenantId, Guid validationId, string stepKey, int stepOrder,
        CancellationToken cancellationToken = default);

    Task RecordRunFinishedAsync(
        long runId, DoTheDoRunStatus status, Guid? novaradAuditId, string? errorMessage,
        CancellationToken cancellationToken = default);

    // Templates
    Task<IReadOnlyList<TechNotesTemplate>> ListActiveTemplatesAsync(
        Guid tenantId, CancellationToken cancellationToken = default);

    Task<TechNotesTemplate> CreateTemplateAsync(
        Guid tenantId, Guid createdByUserId, string label, string body, int sortOrder,
        CancellationToken cancellationToken = default);

    Task<TechNotesTemplate?> UpdateTemplateAsync(
        Guid tenantId, long templateId, string label, string body, int sortOrder,
        CancellationToken cancellationToken = default);

    /// <summary>Soft-delete: marks the template inactive so it stops appearing on step 3.</summary>
    Task<bool> DeactivateTemplateAsync(
        Guid tenantId, long templateId, CancellationToken cancellationToken = default);

    // Ready-studies projector upserts
    Task UpsertReadyStudiesAsync(
        Guid tenantId, IReadOnlyCollection<ReadyStudy> studies,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove rows from ready_studies that the projector did not refresh this pass
    /// AND that have no in-progress validation pinning them.
    /// </summary>
    Task PruneStaleReadyStudiesAsync(
        Guid tenantId, DateTimeOffset olderThan, CancellationToken cancellationToken = default);
}

/// <summary>What changed on a wizard step submission. All fields optional — null means "no change".</summary>
/// <remarks>
/// The patient-action block (<see cref="PatientAction"/>, <see cref="Correction"/>,
/// <see cref="ReassignTargetPatientId"/>) is written atomically only when
/// <see cref="PatientAction"/> is non-null — that lets step 1 overwrite or clear the
/// whole block while other steps leave it untouched.
/// </remarks>
public sealed record ValidationStepUpdate(
    long? NovaradOrderId = null,
    long? NovaradPatientId = null,
    string? Reason = null,
    string? TechNotes = null,
    long? ReferringPhysicianId = null,
    long[]? ComparisonStudyIds = null,
    bool? CreateOrderRequested = null,
    PatientAction? PatientAction = null,
    PatientCorrection? Correction = null,
    long? ReassignTargetPatientId = null);
