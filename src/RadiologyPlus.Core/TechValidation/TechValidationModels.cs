namespace RadiologyPlus.Core.TechValidation;

/// <summary>
/// Wizard step, 1..6. Comparisons (3) was inserted between Order and Reason so the tech
/// can flag prior studies for comparison before finalizing. Numeric values shifted:
/// old Reason=3/Review=4/DoTheDo=5 → new 4/5/6. The DB check constraint follows in 0010.
/// </summary>
public enum WizardStep : short
{
    Study = 1,
    Order = 2,
    Comparisons = 3,
    Reason = 4,
    Review = 5,
    DoTheDo = 6,
}

public enum ValidationStatus : short
{
    Open = 1,
    InProgress = 2,
    Submitted = 3,
    Completed = 4,
    Failed = 5,
    Cancelled = 6,
}

/// <summary>What (if anything) the tech chose to do about the study's patient on step 1.</summary>
public enum PatientAction : short
{
    None = 0,
    /// <summary>Correct demographics on the study's current patient (UPDATE pacs.patients).</summary>
    EditInPlace = 1,
    /// <summary>Move the study to a different existing patient (UPDATE pacs.studies.patient).</summary>
    Reassign = 2,
}

/// <summary>Edit-in-place demographic correction for the study's current patient. Null fields mean "leave unchanged".</summary>
public sealed record PatientCorrection(
    string? LastName,
    string? FirstName,
    string? MiddleName,
    DateOnly? BirthDate,
    string? Gender);

/// <summary>One row on the Tech Validation worklist.</summary>
public sealed record ReadyStudy(
    long NovaradStudyId,
    int FacilityId,
    string StudyUid,
    string? Accession,
    DateTimeOffset? StudyDate,
    string? Modality,
    string? Custom3,
    string? StudyDescription,
    long NovaradPatientId,
    string? PatientPid,
    string? PatientLastName,
    string? PatientFirstName,
    DateOnly? PatientBirthDate,
    string? PatientGender,
    DateTimeOffset? LastImageProcessedDate,
    DateTimeOffset ProjectedAt,
    // Join from validations (null when no open wizard exists)
    Guid? InProgressValidationId,
    ValidationStatus? InProgressStatus,
    Guid? InProgressStartedBy,
    // Hydrated display name for InProgressStartedBy (display_name with username fallback).
    string? InProgressStartedByDisplay,
    DateTimeOffset? InProgressStartedAt);

/// <summary>Active wizard state. One row per (tenant, study) while a tech is working it.</summary>
public sealed record ValidationRecord(
    Guid ValidationId,
    Guid TenantId,
    long NovaradStudyId,
    long? NovaradOrderId,
    long? NovaradPatientId,
    WizardStep CurrentStep,
    ValidationStatus Status,
    string? Reason,
    string? TechNotes,
    long? ReferringPhysicianId,
    long[] ComparisonStudyIds,
    bool CreateOrderRequested,
    Guid StartedByUserId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    PatientAction PatientAction,
    PatientCorrection? Correction,
    long? ReassignTargetPatientId);

/// <summary>Order row pulled from ris.orders for the order-selection step.</summary>
public sealed record CandidateOrder(
    long OrderId,
    string PatientId,
    string AccessionNumber,
    string Status,
    string? Description,
    string? PhysicianReason,
    string? Notes,
    long? ReferringPhysicianId,
    DateTimeOffset? CreationDate);

/// <summary>Comparison-eligible study (older study for the same patient or fuzzy name match).</summary>
public sealed record ComparisonCandidate(
    long NovaradStudyId,
    string StudyUid,
    string? Accession,
    DateTimeOffset? StudyDate,
    string? Modality,
    long NovaradPatientId,
    string? PatientLastName,
    string? PatientFirstName,
    DateOnly? PatientBirthDate);

/// <summary>A candidate target patient when reassigning a study to a different existing patient.</summary>
public sealed record PatientSearchResult(
    long NovaradPatientId,
    string? Pid,
    string? LastName,
    string? FirstName,
    string? MiddleName,
    DateOnly? BirthDate,
    string? Gender,
    DateTimeOffset? RecentStudyDate);

public sealed record TechNotesTemplate(long TemplateId, string Label, string Body, int SortOrder);

/// <summary>
/// One row of the patient's prior-study jacket for the wizard's Comparisons step.
/// <see cref="Suggested"/> = true when the heuristic (same modality + description
/// similarity) crosses the suggestion threshold; the UI pins those rows at the top.
/// </summary>
public sealed record PatientJacketEntry(
    long NovaradStudyId,
    string StudyUid,
    string? Accession,
    DateTimeOffset? StudyDate,
    string? Modality,
    string? Description,
    double Score,
    bool Suggested);

/// <summary>One Do-the-Do orchestrator step.</summary>
public sealed record DoTheDoRun(
    long RunId,
    Guid ValidationId,
    string StepKey,
    int StepOrder,
    DoTheDoRunStatus Status,
    Guid? NovaradAuditId,
    string? ErrorMessage,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

public enum DoTheDoRunStatus : short
{
    Started = 1,
    Succeeded = 2,
    Failed = 3,
}
