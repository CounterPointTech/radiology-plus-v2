namespace RadiologyPlus.Core.Billing;

/// <summary>One row of the per-tenant CPT master.</summary>
public sealed record CptCode(
    short Year,
    string Code,
    string Description,
    decimal WorkRvu,
    string? Notes,
    bool IsActive,
    long? ImportedFromImportId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Per-tenant, optionally per-facility RVU override.</summary>
public sealed record RvuOverride(
    long OverrideId,
    short Year,
    string Code,
    long? FacilityId,                 // NULL = tenant-wide override
    decimal OverrideWorkRvu,
    string? Note,
    Guid? CreatedByUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Header for a single CPT-master import run.</summary>
public sealed record CptImport(
    long ImportId,
    string FileName,
    string SheetName,
    short Year,
    int ParsedRows,
    int InsertedRows,
    int UpdatedRows,
    int SkippedRows,
    IReadOnlyList<CptImportError> Errors,
    Guid RanByUserId,
    DateTimeOffset RanAt);

/// <summary>Per-row failure surfaced inside CptImport.Errors. Stored as JSONB.</summary>
public sealed record CptImportError(int Row, string? Cpt, string Message);

/// <summary>Per-row payload the importer hands to the repository.</summary>
public sealed record CptCodeUpsert(
    string Code,
    string Description,
    decimal WorkRvu,
    string? Notes);

// ============================================================================
// Phase 2 — service_code → CPT crosswalk
// ============================================================================

/// <summary>
/// One row of the per-tenant <c>service_code → cpt_code</c> crosswalk.
/// Status: 1=approved (credit through this mapping), 2=suppressed (never credit).
/// Source: 1=manual, 2=accepted suggestion, 3=bulk import.
/// </summary>
public sealed record ServiceCodeMapping(
    long CrosswalkId,
    string ServiceCode,
    string CptCode,
    short Status,
    short Source,
    string? Note,
    string? ApprovedForDescription,
    long AppliedCount,
    DateTimeOffset? LastUsedAt,
    Guid CreatedByUserId,
    string? CreatedByDisplayName,
    Guid? UpdatedByUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Write-shape for create/update of a single crosswalk row.</summary>
public sealed record ServiceCodeMappingUpsert(
    string ServiceCode,
    string CptCode,
    short Source,                                       // 1 manual / 2 suggested / 3 bulk
    short? Status = null,                               // null on insert → defaults to 1 (approved)
    string? Note = null,
    string? ApprovedForDescription = null);

/// <summary>Returned from UpsertCrosswalkAsync.</summary>
public sealed record CrosswalkUpsertResult(ServiceCodeMapping Mapping, bool Inserted);

/// <summary>One suggester candidate for a service_code.</summary>
public sealed record CrosswalkSuggestion(
    string CptCode,
    string Description,
    decimal WorkRvu,
    decimal Score,                                      // 0..1; 1.0 for exact-code hit
    string HitKind);                                    // "exact_code" | "description"

/// <summary>One row of a bulk-import payload.</summary>
public sealed record BulkImportRow(string ServiceCode, string CptCode, string? Note);

/// <summary>Per-row outcome of a bulk import.</summary>
public sealed record BulkImportRowResult(
    string ServiceCode,
    string Outcome,                                     // "inserted" | "updated" | "skipped" | "error"
    string? Error);

/// <summary>Bulk-import summary returned to the API client.</summary>
public sealed record BulkImportResult(
    int Inserted,
    int Updated,
    int Skipped,
    int Errors,
    IReadOnlyList<BulkImportRowResult> Rows);

/// <summary>
/// One per-report detail row backing a reconciliation line. Returned from the
/// drill-down endpoint when the user expands a (physician × CPT × site) row to
/// see which reports actually rolled up into the credit.
/// Patient/study columns are nullable because the ris.orders.accession_number →
/// pacs.studies.accession join can miss when the study row was never imaged or
/// was soft-deleted.
/// </summary>
public sealed record ReconciliationDetailRow(
    long ReportId,
    DateTimeOffset SignedAt,
    long OrderId,
    string SiteCode,
    string? Accession,
    string? StudyUid,
    DateTimeOffset? StudyDate,
    string? Modality,
    long? NovaradPatientId,
    string? PatientPid,
    string? PatientLastName,
    string? PatientFirstName,
    DateOnly? PatientBirthDate,
    string? PatientGender);
