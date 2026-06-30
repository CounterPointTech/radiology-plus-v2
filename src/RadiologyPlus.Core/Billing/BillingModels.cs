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

/// <summary>Per-tenant RVU override, optionally scoped to a single Novarad site.</summary>
public sealed record RvuOverride(
    long OverrideId,
    short Year,
    string Code,
    long? FacilityId,                 // vestigial (always NULL); superseded by SiteCode
    string? SiteCode,                 // NULL = tenant-wide override; set = site-specific (raw Novarad site_code)
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
// Item 1.2 — CMS RVU source-of-truth (billing.rvu_values)
// ============================================================================

/// <summary>
/// One row of the per-tenant CMS PFS Relative Value snapshot (PPRRVU). A HCPCS can
/// carry up to three rows by <c>Modifier</c>: '' (global), '26' (professional),
/// 'TC' (technical). <c>WorkRvu</c> is the figure reconciliation credits against.
/// </summary>
public sealed record RvuValue(
    short Year,
    char Quarter,
    string Hcpcs,
    string Modifier,
    string? Description,
    decimal WorkRvu,
    decimal? PeRvuNonFac,
    decimal? PeRvuFac,
    decimal? MpRvu,
    decimal? TotalNonFac,
    decimal? TotalFac,
    string? StatusCode,
    string? GlobalDays,
    DateOnly? EffectiveFrom,
    long? SourceImportId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Per-row payload the CMS importer hands to the repository.</summary>
public sealed record RvuValueUpsert(
    string Hcpcs,
    string Modifier,
    string? Description,
    decimal WorkRvu,
    decimal? PeRvuNonFac,
    decimal? PeRvuFac,
    decimal? MpRvu,
    decimal? TotalNonFac,
    decimal? TotalFac,
    string? StatusCode,
    string? GlobalDays);

/// <summary>Header for a single CMS RVU import run (mirror of <see cref="CptImport"/>).</summary>
public sealed record RvuImport(
    long ImportId,
    string FileName,
    short Year,
    char Quarter,
    int ParsedRows,
    int InsertedRows,
    int UpdatedRows,
    int SkippedRows,
    IReadOnlyList<CptImportError> Errors,
    Guid RanByUserId,
    DateTimeOffset RanAt);

/// <summary>
/// Audit header for a single M*Modal RVU write-back run (mirror of <see cref="RvuImport"/>).
/// <c>DryRun</c> = true rows are previews (no write). Counts: <c>MatchedRows</c> effective
/// codes that exist in M*Modal, <c>UpdatedRows</c> whose RVU actually changed,
/// <c>UnchangedRows</c> already equal, <c>MissingRows</c> with no active M*Modal row.
/// </summary>
public sealed record RvuSyncRun(
    long SyncRunId,
    short Year,
    char Quarter,
    bool DryRun,
    int MatchedRows,
    int UpdatedRows,
    int UnchangedRows,
    int MissingRows,
    bool Success,
    string? ErrorMessage,
    Guid RanByUserId,
    DateTimeOffset RanAt);

/// <summary>
/// Write-shape for upserting a manual RVU override (the top layer of the
/// rvu_overrides → rvu_values → cpt_codes precedence). <c>Code</c> may be a single
/// HCPCS or a <c>;</c>-delimited bundle string — both resolve in the reconciliation
/// overlay. <c>SiteCode</c> NULL = tenant-wide override; a raw Novarad site_code =
/// site-specific (it wins over the tenant-wide override at that site during
/// reconciliation: site &gt; tenant &gt; CMS &gt; master).
/// </summary>
public sealed record RvuOverrideUpsert(
    short Year,
    string Code,
    decimal OverrideWorkRvu,
    string? Note,
    string? SiteCode = null);

/// <summary>Result of <c>UpsertRvuOverrideAsync</c>: the persisted row + whether it was an insert.</summary>
public sealed record RvuOverrideUpsertResult(RvuOverride Override, bool Inserted);

/// <summary>
/// One CPT-master row joined to the CMS source of truth, for the RVU management view's
/// "CMS check" surface. For a single code, <c>CmsWorkRvu</c> is the CMS PPRRVU work RVU
/// (modifier '', latest loaded quarter). For a bundle, it's the SUM of the CMS work RVUs
/// of its <c>;</c>-split components, with <c>BundleParts</c>/<c>BundleMatched</c>
/// describing coverage. <c>EffectiveWorkRvu</c> is exactly what reconciliation would
/// credit (override → CMS → master), taken from the same overlay the credit path uses, so
/// the UI never shows a number that disagrees with reconciliation. <c>Verdict</c> is one
/// of: <c>matches</c> | <c>differs</c> | <c>not_in_cms</c> | <c>status_gated</c> (single);
/// <c>matches_sum</c> | <c>differs_sum</c> | <c>partial</c> (bundle).
/// </summary>
public sealed record CptMasterCmsRow(
    short Year,
    string Code,
    bool IsBundle,
    string Description,
    decimal MasterWorkRvu,
    decimal? CmsWorkRvu,
    string? CmsStatus,
    int? BundleParts,
    int? BundleMatched,
    decimal? OverrideWorkRvu,
    decimal EffectiveWorkRvu,
    string Verdict);

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
    string? SiteCode,                                   // null = tenant-wide default; set = site-specific (raw Novarad site_code)
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
    string? ApprovedForDescription = null,
    string? SiteCode = null);                           // null = tenant-wide default; set = site-specific

/// <summary>Returned from UpsertCrosswalkAsync.</summary>
public sealed record CrosswalkUpsertResult(ServiceCodeMapping Mapping, bool Inserted);

/// <summary>One suggester candidate for a service_code.</summary>
public sealed record CrosswalkSuggestion(
    string CptCode,
    string Description,
    decimal WorkRvu,
    decimal Score,                                      // 0..1; 1.0 for exact-code hit
    string HitKind);                                    // "exact_code" | "description"

/// <summary>One row of a bulk-import payload. SiteCode null = tenant-wide default.</summary>
public sealed record BulkImportRow(string ServiceCode, string CptCode, string? Note, string? SiteCode = null);

/// <summary>Per-row outcome of a bulk import. SiteCode echoes the row's scope so callers
/// can disambiguate a code applied to several sites in one batch.</summary>
public sealed record BulkImportRowResult(
    string ServiceCode,
    string Outcome,                                     // "inserted" | "updated" | "skipped" | "error"
    string? Error,
    string? SiteCode = null);

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
/// <summary>
/// Full report content + the surrounding patient/study/signing metadata, used by the
/// reconciliation drill-down's per-report viewer. <c>ReportFormat</c> mirrors Novarad's
/// <c>ris.reports.report_format</c> ("HTML" | "TEXT" | "AUDIO" | "AUTOTEXT") so the UI
/// can decide whether to render the body as HTML or as preformatted text.
/// </summary>
public sealed record ReportContent(
    long ReportId,
    DateTimeOffset? SignedAt,
    long? SigningPhysicianId,
    string? SigningPhysicianName,
    string? Accession,
    DateTimeOffset? StudyDate,
    string? Modality,
    long? NovaradPatientId,
    string? PatientPid,
    string? PatientLastName,
    string? PatientFirstName,
    DateOnly? PatientBirthDate,
    string? PatientGender,
    string? ReportFormat,
    string? ReportText);

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
