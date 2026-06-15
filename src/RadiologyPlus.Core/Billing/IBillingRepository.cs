namespace RadiologyPlus.Core.Billing;

public interface IBillingRepository
{
    /// <summary>
    /// Atomically:
    ///   1. Insert a billing.cpt_imports header row, returning its import_id.
    ///   2. Bulk-upsert the supplied CPT rows into billing.cpt_codes for (tenant, year),
    ///      tagging each row with the new import_id.
    ///   3. Update the header's parsed/inserted/updated/skipped counts and error list.
    /// Returns the persisted header for the caller to hand back to the API client.
    /// </summary>
    Task<CptImport> ImportCptMasterAsync(
        Guid tenantId,
        Guid runByUserId,
        string fileName,
        string sheetName,
        short year,
        IReadOnlyList<CptCodeUpsert> rows,
        IReadOnlyList<CptImportError> parseErrors,
        CancellationToken cancellationToken = default);

    /// <summary>List the active CPT master for a tenant + year, with optional fuzzy match.</summary>
    Task<IReadOnlyList<CptCode>> ListCptCodesAsync(
        Guid tenantId,
        short year,
        string? search,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Latest N import-run audit rows for a tenant.</summary>
    Task<IReadOnlyList<CptImport>> ListRecentImportsAsync(
        Guid tenantId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically insert a billing.rvu_imports header, bulk-upsert the parsed CMS
    /// PPRRVU rows into billing.rvu_values for (tenant, year, quarter), and update the
    /// header counts. Mirrors <see cref="ImportCptMasterAsync"/>. Returns the header.
    /// </summary>
    Task<RvuImport> ImportRvuValuesAsync(
        Guid tenantId,
        Guid runByUserId,
        string fileName,
        short year,
        char quarter,
        IReadOnlyList<RvuValueUpsert> rows,
        IReadOnlyList<CptImportError> parseErrors,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// List/search the CMS RVU snapshot for a tenant. Optional year/quarter scope and
    /// a fuzzy match over HCPCS + description. Read-only; for the RVU browse surface.
    /// </summary>
    Task<IReadOnlyList<RvuValue>> ListRvuValuesAsync(
        Guid tenantId,
        short? year,
        char? quarter,
        string? search,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Patch a single CPT row's <c>work_rvu</c>, <c>description</c>, or <c>notes</c>.
    /// Pass <c>null</c> for any field that shouldn't change. Returns the row
    /// before and after the change so the caller can log a diff in audit.
    /// Throws <see cref="KeyNotFoundException"/> if the row doesn't exist.
    /// </summary>
    Task<CptCodeChange> UpdateCptCodeAsync(
        Guid tenantId,
        short year,
        string code,
        decimal? workRvu,
        string? description,
        string? notes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Run a bundle-aware reconciliation over the supplied Novarad service-line
    /// rows: group by (report × procedure), match the per-procedure CPT set
    /// against bundle rows in <c>billing.cpt_codes</c> (set-equality on
    /// <c>;</c>-split codes), credit the bundle once when matched, otherwise
    /// credit each CPT against its singleton master row. Persists the run
    /// header + per-(physician × site × CPT) line items atomically and returns
    /// the materialized run.
    /// </summary>
    Task<ReconciliationRun> RunReconciliationAsync(
        Guid tenantId,
        Guid runByUserId,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        long? facilityId,
        short runKind,                              // 1 preview, 2 final
        IReadOnlyList<SignedProcedureLineItem> source,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Phase 1 data-quality report: of the signed service-line rows supplied, which
    /// codes wouldn't be credited because they're absent from the CPT master? Applies
    /// the same per-procedure bundle/singleton logic as reconciliation, then returns
    /// one classified row per distinct uncredited code (with its Novarad description
    /// and impact counts) so a human can map it. Read-only.
    /// </summary>
    Task<IReadOnlyList<UnmappedServiceCode>> BuildUnmappedReportAsync(
        Guid tenantId,
        IReadOnlyList<SignedProcedureLineItem> source,
        CancellationToken cancellationToken = default);

    // ========================================================================
    // Phase 2 — service_code → CPT crosswalk
    // ========================================================================

    /// <summary>List crosswalk rows for the tenant, optionally filtered by status.</summary>
    /// <param name="status">1=approved only, 2=suppressed only, null=all.</param>
    Task<IReadOnlyList<ServiceCodeMapping>> ListCrosswalkAsync(
        Guid tenantId,
        short? status,
        CancellationToken cancellationToken = default);

    /// <summary>Get a single crosswalk row by service_code; null if not present.</summary>
    Task<ServiceCodeMapping?> GetCrosswalkAsync(
        Guid tenantId,
        string serviceCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Insert-or-update a single crosswalk row. Returns the persisted mapping and
    /// whether it was an insert (true) vs. an update (false), so the caller can
    /// emit the right audit action.
    /// </summary>
    Task<CrosswalkUpsertResult> UpsertCrosswalkAsync(
        Guid tenantId,
        Guid userId,
        ServiceCodeMappingUpsert upsert,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Flip a crosswalk row's status (1 approved ↔ 2 suppressed). Throws
    /// <see cref="KeyNotFoundException"/> if the row doesn't exist.
    /// </summary>
    Task<ServiceCodeMapping> SetCrosswalkStatusAsync(
        Guid tenantId,
        Guid userId,
        string serviceCode,
        short status,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk upsert from a CSV import. <paramref name="updateOnConflict"/> chooses
    /// between "skip" (false) and "update" (true) when a (tenant_id, service_code)
    /// row already exists. Returns per-row outcomes plus aggregate counts.
    /// </summary>
    Task<BulkImportResult> BulkUpsertCrosswalkAsync(
        Guid tenantId,
        Guid userId,
        IReadOnlyList<BulkImportRow> rows,
        bool updateOnConflict,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Suggest CPT candidates for a Novarad service_code. Combines an exact-code
    /// branch (when a trailing-suffix strip yields a code in the master) with a
    /// trigram-similarity branch over <c>billing.cpt_codes.description</c>.
    /// Returns top-N ranked candidates. If a suppressed crosswalk row already
    /// exists for the service_code, returns an empty list (caller surfaces the
    /// "this code is suppressed" hint).
    /// </summary>
    /// <param name="year">CPT master year; null = latest available year for the tenant.</param>
    Task<IReadOnlyList<CrosswalkSuggestion>> SuggestCrosswalkAsync(
        Guid tenantId,
        short? year,
        string serviceCode,
        string? novaradDescription,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Return the persisted <c>novarad_report_ids</c> for a single reconciliation
    /// line, identified by its composite natural key (run × physician × cpt × site).
    /// Returns <c>null</c> when no such line exists for the tenant. The caller
    /// hands the IDs to <see cref="INovaradReportsReader.ReadReportDetailsAsync"/>
    /// to materialize the drill-down rows.
    /// </summary>
    Task<IReadOnlyList<long>?> GetReconciliationLineReportIdsAsync(
        Guid tenantId,
        long runId,
        long novaradPhysicianId,
        string cptCode,
        string siteCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-materialize a previously-persisted reconciliation run + its full line items
    /// so it can be re-served (e.g. for the xlsx export). Returns null when the run
    /// doesn't belong to the tenant (or doesn't exist). Read-only.
    /// </summary>
    Task<ReconciliationRun?> GetRunWithLinesAsync(
        Guid tenantId,
        long runId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A Novarad service_code that appeared on a signed report but isn't in the CPT
/// master, so it earned no RVU credit. <c>Kind</c> is "cpt_missing_from_master"
/// (looks like a real CPT — add it to the sheet) or "non_cpt_service_code"
/// (internal/LOINC code — map it to a CPT in Novarad or via a crosswalk).
/// </summary>
public sealed record UnmappedServiceCode(
    string Code,
    short Year,
    string Kind,
    string? Description,
    int ReportCount,
    int ServiceLineCount,
    bool LooksLikeCpt,
    // Which Novarad sites this code appeared at, with per-site impact counts.
    IReadOnlyList<UnmappedFacilityBreakdown> Facilities,
    // Best CPT-master match for this code (top suggestion), surfaced inline so the
    // user sees a candidate CPT + its RVU without opening the Map dialog. Null when
    // there's no confident match. SuggestionHitKind = "exact_code" | "description".
    string? SuggestedCpt,
    string? SuggestedCptDescription,
    decimal? SuggestedWorkRvu,
    string? SuggestionHitKind);

/// <summary>
/// Per-site breakdown of an unmapped code's impact — which Novarad <c>site_code</c>
/// it appeared at and how many reports / service lines there. <c>FacilityId</c> is
/// the local <c>tenancy.facilities</c> id when the site_code is mapped (often null —
/// the customer's site_codes aren't all mapped to facility rows).
/// </summary>
public sealed record UnmappedFacilityBreakdown(
    string SiteCode,
    long? FacilityId,
    int ReportCount,
    int ServiceLineCount);

/// <summary>Before/after pair returned from UpdateCptCodeAsync for audit.</summary>
public sealed record CptCodeChange(CptCode Before, CptCode After);

// ============================================================================
// Reconciliation result shapes
// ============================================================================

/// <summary>One materialized reconciliation run + its line items.</summary>
public sealed record ReconciliationRun(
    long RunId,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    long? FacilityId,
    short RunKind,
    int TotalReports,
    int TotalRadiologists,
    decimal TotalWorkRvu,
    int StatReportCount,                                    // distinct credited reports flagged STAT in Novarad
    IReadOnlyList<ReconciliationLineItem> LineItems,
    IReadOnlyList<ReconciliationNote> Notes,
    IReadOnlyList<ReconciliationFacilitySummary> FacilitySummaries,
    Guid GeneratedByUserId,
    DateTimeOffset GeneratedAt);

/// <summary>
/// Per-facility rollup for a reconciliation run: distinct signed reports credited
/// at that site, and how many of them were flagged STAT in Novarad
/// (<c>ris.order_procedures.stat_flag</c>). Computed over the same credited
/// population as the run totals, so the per-facility subtotals reconcile.
/// </summary>
public sealed record ReconciliationFacilitySummary(
    long? FacilityId,
    string SiteCode,
    int TotalReports,
    int StatReportCount);

/// <summary>
/// One per-(physician × site × CPT-or-bundle) credit line.
/// <c>CptCode</c> carries the bundle string verbatim when this line credits a
/// bundle (e.g. <c>"70492;71270;74178"</c>).
/// </summary>
public sealed record ReconciliationLineItem(
    long LineId,
    long NovaradPhysicianId,
    string PhysicianDisplayName,
    string SiteCode,
    long? FacilityId,
    string CptCode,
    string? CptDescription,
    int ReportCount,
    decimal Units,
    decimal WorkRvuPerUnit,
    decimal WorkRvuTotal,
    decimal? NovaradRvuWork,
    bool RvuMismatch,
    IReadOnlyList<long> NovaradReportIds,
    // The STAT subset of NovaradReportIds (ris.order_procedures.stat_flag). Surfaced
    // so the UI can derive per-radiologist STAT subtotals and badge STAT reports in
    // the drill-down without a second query — mirrors the per-facility rollup.
    IReadOnlyList<long> NovaradStatReportIds);

/// <summary>
/// Non-fatal anomaly surfaced during reconciliation (missing master row,
/// inconsistent Novarad RVU across sources, etc.).
/// </summary>
public sealed record ReconciliationNote(string Kind, string Message);
