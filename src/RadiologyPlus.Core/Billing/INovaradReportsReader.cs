namespace RadiologyPlus.Core.Billing;

/// <summary>
/// Read-side access into a tenant's Novarad RIS for billing reconciliation.
/// Joins ris.reports → ris.order_procedures → ris.orders for the signed-report
/// projection, and ris.order_procedure_service_lines → ris.billing_service_codes
/// for the per-procedure CPT breakdown.
/// </summary>
public interface INovaradReportsReader
{
    /// <summary>
    /// Per-radiologist signed-report counts in the (from, to) window, optionally
    /// scoped to a Novarad site_code. Window is inclusive-from, exclusive-to.
    /// Returns one row per (signing_physician_id × site_code).
    /// </summary>
    Task<IReadOnlyList<SignedReportSummary>> ReadSignedReportCountsAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        string? siteCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Per-procedure CPT line items for every signed report in the window.
    /// One row per service line, so a multi-CPT procedure produces multiple rows
    /// sharing the same (ReportId, ProcedureId). Window is inclusive-from,
    /// exclusive-to. Empty-result is normal — the live clone currently has zero
    /// rows in ris.order_procedure_service_lines for its two signed reports.
    /// </summary>
    Task<IReadOnlyList<SignedProcedureLineItem>> ReadSignedProcedureLineItemsAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        string? siteCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One row per report in <paramref name="reportIds"/>, joined to its order
    /// and (when present) the corresponding pacs.studies + pacs.patients rows
    /// via accession_number. Used by the reconciliation drill-down to surface
    /// per-report patient demographics + study metadata behind a credited line.
    /// Empty input → empty output.
    /// </summary>
    Task<IReadOnlyList<ReconciliationDetailRow>> ReadReportDetailsAsync(
        IReadOnlyList<long> reportIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pull the full report body + signing metadata + patient/study demographics for
    /// a single Novarad report. Used by the reconciliation drill-down's inline report
    /// viewer. Returns null when the report doesn't exist.
    /// </summary>
    Task<ReportContent?> ReadReportFullAsync(
        long reportId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One row of the signed-report projection — per (radiologist × facility),
/// pre-CPT breakdown.
/// </summary>
public sealed record SignedReportSummary(
    long SigningPhysicianId,
    string PhysicianDisplayName,    // "First Last", or "Physician #<id>" if no person record
    string SiteCode,                // Novarad's site_code as it appears on ris.orders
    int SignedReportCount,
    DateTimeOffset EarliestSignedAt,
    DateTimeOffset LatestSignedAt);

/// <summary>
/// One service line on a signed report. Multiple rows can share the same
/// (ReportId, ProcedureId) when a procedure carries multiple CPTs. CptCode is
/// always a single Novarad service_code (Novarad stores one CPT per service line);
/// bundle resolution happens downstream by collapsing the per-procedure set.
/// </summary>
public sealed record SignedProcedureLineItem(
    long ReportId,
    long ProcedureId,
    long SigningPhysicianId,
    string PhysicianDisplayName,
    string SiteCode,
    DateTimeOffset SignedAt,
    string CptCode,
    decimal Units,
    string? CptDescription,
    decimal? NovaradRvuWork);
