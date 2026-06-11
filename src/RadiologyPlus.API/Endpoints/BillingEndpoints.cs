using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using RadiologyPlus.Core.Audit;
using RadiologyPlus.Core.Billing;
using RadiologyPlus.Core.Identity;
using RadiologyPlus.Data.Billing;

namespace RadiologyPlus.API.Endpoints;

public static class BillingEndpoints
{
    // Spreadsheets are small (Amber's 2026 file is ~250 KB) but cap at 5 MB
    // to keep an attacker from streaming 4 GB through the parser.
    private const long MaxUploadBytes = 5 * 1024 * 1024;

    public static IEndpointRouteBuilder MapBillingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/billing")
            .WithTags("Billing")
            .RequireAuthorization();

        group.MapPost("/cpt-master/import", ImportCptMasterAsync)
             .WithName("BillingImportCptMaster")
             .DisableAntiforgery();                      // bearer-token auth + same-origin CORS is our guard

        group.MapGet("/cpt-master", ListCptMasterAsync).WithName("BillingListCptMaster");
        group.MapGet("/cpt-master/imports", ListCptImportsAsync).WithName("BillingListImports");
        group.MapPatch("/cpt-master/{code}", PatchCptCodeAsync).WithName("BillingPatchCptCode");
        group.MapGet("/reconciliation/preview", PreviewReconciliationAsync).WithName("BillingReconciliationPreview");
        group.MapGet("/reconciliation/unmapped", UnmappedCodesAsync).WithName("BillingReconciliationUnmapped");
        group.MapPost("/reconciliation/run", RunReconciliationAsync).WithName("BillingReconciliationRun");
        group.MapGet("/reconciliation/{runId:long}/detail", ReconciliationLineDetailAsync).WithName("BillingReconciliationLineDetail");
        group.MapGet("/reconciliation/{runId:long}/export", ExportReconciliationAsync).WithName("BillingReconciliationExport");
        group.MapGet("/reconciliation/report/{reportId:long}", GetReportFullAsync).WithName("BillingReportFull");

        // Phase 2 — service_code → CPT crosswalk
        group.MapGet("/crosswalk", ListCrosswalkAsync).WithName("BillingCrosswalkList");
        group.MapGet("/crosswalk/suggestions", SuggestCrosswalkAsync).WithName("BillingCrosswalkSuggestions");
        group.MapPost("/crosswalk", CreateCrosswalkAsync).WithName("BillingCrosswalkCreate");
        group.MapPut("/crosswalk/{serviceCode}", UpdateCrosswalkAsync).WithName("BillingCrosswalkUpdate");
        group.MapPost("/crosswalk/bulk", BulkImportCrosswalkAsync).WithName("BillingCrosswalkBulkImport");

        return app;
    }

    [Authorize]
    private static async Task<IResult> PreviewReconciliationAsync(
        ICurrentUser currentUser,
        INovaradReportsReader reader,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? site,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessBilling()) return Results.Forbid();

        // Default window: last 60 days (rough proxy for "current 2-month billing cycle").
        // Callers should pass explicit from/to once we know what Amber's billing-window boundary actually is.
        // Window is local wall-clock to match Novarad's signed_date (timestamp without time zone).
        var fromLocal = from ?? DateTimeOffset.Now.AddDays(-60);
        var toLocal   = to   ?? DateTimeOffset.Now;
        if (toLocal <= fromLocal)
            return Results.BadRequest(new { error = "`to` must be after `from`." });
        if (toLocal - fromLocal > TimeSpan.FromDays(370))
            return Results.BadRequest(new { error = "Window cannot exceed 370 days." });

        var rows = await reader.ReadSignedReportCountsAsync(fromLocal, toLocal, site, ct);

        return Results.Ok(new
        {
            from = fromLocal,
            to = toLocal,
            site,
            totalReports = rows.Sum(r => r.SignedReportCount),
            totalRadiologists = rows.Select(r => r.SigningPhysicianId).Distinct().Count(),
            rows,
        });
    }

    [Authorize]
    private static async Task<IResult> UnmappedCodesAsync(
        ICurrentUser currentUser,
        INovaradReportsReader reader,
        IBillingRepository repo,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? site,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessBilling()) return Results.Forbid();

        // Window is local wall-clock so unmapped + reconciliation always agree on the
        // window and match Novarad's signed_date (timestamp without time zone).
        var fromLocal = from ?? DateTimeOffset.Now.AddDays(-60);
        var toLocal   = to   ?? DateTimeOffset.Now;
        if (toLocal <= fromLocal)
            return Results.BadRequest(new { error = "`to` must be after `from`." });
        if (toLocal - fromLocal > TimeSpan.FromDays(370))
            return Results.BadRequest(new { error = "Window cannot exceed 370 days." });

        var source = await reader.ReadSignedProcedureLineItemsAsync(fromLocal, toLocal, site, ct);
        var unmapped = await repo.BuildUnmappedReportAsync(user.TenantId, source, ct);

        return Results.Ok(new
        {
            from = fromLocal,
            to = toLocal,
            site,
            totalCodes = unmapped.Count,
            totalReportsUncredited = unmapped.Sum(u => u.ReportCount),
            codes = unmapped,
        });
    }

    [Authorize]
    private static async Task<IResult> RunReconciliationAsync(
        RunReconciliationRequest req,
        ICurrentUser currentUser,
        INovaradReportsReader reader,
        IBillingRepository repo,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessBilling()) return Results.Forbid();

        var fromLocal = req.From;
        var toLocal   = req.To;
        if (toLocal <= fromLocal)
            return Results.BadRequest(new { error = "`to` must be after `from`." });
        if (toLocal - fromLocal > TimeSpan.FromDays(370))
            return Results.BadRequest(new { error = "Window cannot exceed 370 days." });

        // Phase 2.4 always persists a Final (run_kind=2) run; the schema is also
        // ready for run_kind=1 Preview persistence later if Amber wants
        // headcount snapshots saved alongside the line-item runs.
        const short runKind = 2;

        var source = await reader.ReadSignedProcedureLineItemsAsync(fromLocal, toLocal, req.Site, ct);
        var run = await repo.RunReconciliationAsync(
            user.TenantId, user.UserId,
            fromLocal, toLocal, req.FacilityId, runKind,
            source, ct);

        await audit.WriteSuccessAsync(
            user.TenantId, user, AccessAction.Execute,
            $"billing.reconciliation_runs run_id={run.RunId} kind=Final "
                + $"window=[{fromLocal:o},{toLocal:o}) site={req.Site ?? "(all)"} "
                + $"reports={run.TotalReports} rads={run.TotalRadiologists} "
                + $"workRvu={run.TotalWorkRvu} notes={run.Notes.Count} "
                + $"sourceRows={source.Count}",
            http, ct);

        return Results.Ok(run);
    }

    [Authorize]
    private static async Task<IResult> ExportReconciliationAsync(
        long runId,
        ICurrentUser currentUser,
        IBillingRepository repo,
        IReconciliationExporter exporter,
        [FromQuery] long? physicianId,
        [FromQuery] string? siteCode,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessBilling()) return Results.Forbid();

        var run = await repo.GetRunWithLinesAsync(user.TenantId, runId, ct);
        if (run is null)
            return Results.NotFound(new { error = $"Reconciliation run {runId} not found." });

        var bytes = exporter.Export(run, new ReconciliationExportFilters(physicianId, siteCode));
        var periodLabel = run.PeriodStart.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            + "-" + run.PeriodEnd.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var filename = $"reconciliation-{runId}-{periodLabel}.xlsx";
        return Results.File(
            bytes,
            contentType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileDownloadName: filename);
    }

    [Authorize]
    private static async Task<IResult> GetReportFullAsync(
        long reportId,
        ICurrentUser currentUser,
        INovaradReportsReader reader,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessBilling()) return Results.Forbid();

        var content = await reader.ReadReportFullAsync(reportId, ct);
        return content is null
            ? Results.NotFound(new { error = $"Report {reportId} not found." })
            : Results.Ok(content);
    }

    [Authorize]
    private static async Task<IResult> ReconciliationLineDetailAsync(
        long runId,
        ICurrentUser currentUser,
        IBillingRepository repo,
        INovaradReportsReader reader,
        [FromQuery] long physicianId,
        [FromQuery] string cptCode,
        [FromQuery] string siteCode,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessBilling()) return Results.Forbid();
        if (string.IsNullOrWhiteSpace(cptCode))
            return Results.BadRequest(new { error = "cptCode is required." });
        if (string.IsNullOrWhiteSpace(siteCode))
            return Results.BadRequest(new { error = "siteCode is required." });

        var reportIds = await repo.GetReconciliationLineReportIdsAsync(
            user.TenantId, runId, physicianId, cptCode, siteCode, ct);
        if (reportIds is null)
            return Results.NotFound(new { error = "No reconciliation line for the supplied (run, physician, cpt, site)." });

        // Empty array is a legitimate result (line was credited from a bundle
        // collapse that left no Novarad reports tagged) — return an empty rows[].
        var rows = reportIds.Count == 0
            ? Array.Empty<ReconciliationDetailRow>()
            : (IReadOnlyList<ReconciliationDetailRow>)await reader.ReadReportDetailsAsync(reportIds, ct);

        return Results.Ok(new
        {
            runId,
            physicianId,
            cptCode,
            siteCode,
            reportCount = reportIds.Count,
            rows,
        });
    }

    [Authorize]
    private static async Task<IResult> ImportCptMasterAsync(
        HttpRequest request,
        ICurrentUser currentUser,
        IBillingRepository repo,
        ICptMasterImporter importer,
        [FromQuery] short? year,
        [FromQuery] string? sheet,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessBilling()) return Results.Forbid();

        if (!request.HasFormContentType)
            return Results.BadRequest(new { error = "multipart/form-data required" });

        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file") ?? (form.Files.Count > 0 ? form.Files[0] : null);
        if (file is null || file.Length == 0)
            return Results.BadRequest(new { error = "No file uploaded under field 'file'." });
        if (file.Length > MaxUploadBytes)
            return Results.BadRequest(new { error = $"File exceeds the {MaxUploadBytes / 1024 / 1024} MB upload cap." });

        var resolvedYear = year ?? (short)DateTime.Now.Year;
        // Per Amber's 2026-05-14 spec, the canonical sheet is "RVU". Callers
        // can still override (e.g. ?sheet=2026+mpfs) if a legacy file arrives.
        var resolvedSheet = string.IsNullOrWhiteSpace(sheet) ? "RVU" : sheet;

        await using var stream = file.OpenReadStream();
        var parsed = importer.Parse(stream, resolvedSheet, resolvedYear);

        var header = await repo.ImportCptMasterAsync(
            user.TenantId, user.UserId, file.FileName,
            parsed.SheetName, parsed.Year, parsed.Rows, parsed.Errors, ct);

        return Results.Ok(header);
    }

    [Authorize]
    private static async Task<IResult> ListCptMasterAsync(
        ICurrentUser currentUser,
        IBillingRepository repo,
        [FromQuery] short? year,
        [FromQuery] string? q,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessBilling()) return Results.Forbid();

        var rows = await repo.ListCptCodesAsync(
            user.TenantId,
            year ?? (short)DateTime.Now.Year,
            q,
            Math.Clamp(limit ?? 100, 1, 1000),
            ct);
        return Results.Ok(rows);
    }

    [Authorize]
    private static async Task<IResult> PatchCptCodeAsync(
        string code,
        PatchCptCodeRequest req,
        ICurrentUser currentUser,
        IBillingRepository repo,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessBilling()) return Results.Forbid();

        if (req.WorkRvu is null && req.Description is null && req.Notes is null)
            return Results.BadRequest(new { error = "Nothing to update — supply at least one of workRvu, description, notes." });
        if (req.WorkRvu is < 0)
            return Results.BadRequest(new { error = "workRvu must be non-negative." });

        try
        {
            var change = await repo.UpdateCptCodeAsync(
                user.TenantId, req.Year, code,
                req.WorkRvu, req.Description, req.Notes, ct);

            await audit.WriteSuccessAsync(
                user.TenantId, user, AccessAction.Update,
                $"billing.cpt_codes {req.Year}/{code}: " +
                    $"rvu {change.Before.WorkRvu}→{change.After.WorkRvu}, " +
                    $"desc \"{change.Before.Description}\"→\"{change.After.Description}\"",
                http, ct);

            return Results.Ok(change.After);
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { error = $"No CPT row for {req.Year}/{code}." });
        }
    }

    [Authorize]
    private static async Task<IResult> ListCptImportsAsync(
        ICurrentUser currentUser,
        IBillingRepository repo,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessBilling()) return Results.Forbid();

        var rows = await repo.ListRecentImportsAsync(
            user.TenantId,
            Math.Clamp(limit ?? 25, 1, 200),
            ct);
        return Results.Ok(rows);
    }

    // ========================================================================
    // Phase 2 — service_code → CPT crosswalk
    // ========================================================================

    [Authorize]
    private static async Task<IResult> ListCrosswalkAsync(
        ICurrentUser currentUser,
        IBillingRepository repo,
        [FromQuery] short? status,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessBilling()) return Results.Forbid();
        if (status is not null and not (1 or 2))
            return Results.BadRequest(new { error = "status must be 1 (approved), 2 (suppressed), or omitted (all)." });

        var rows = await repo.ListCrosswalkAsync(user.TenantId, status, ct);
        return Results.Ok(new { totalCodes = rows.Count, rows });
    }

    [Authorize]
    private static async Task<IResult> SuggestCrosswalkAsync(
        ICurrentUser currentUser,
        IBillingRepository repo,
        [FromQuery] string? serviceCode,
        [FromQuery] string? description,
        [FromQuery] short? year,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessBilling()) return Results.Forbid();
        if (string.IsNullOrWhiteSpace(serviceCode))
            return Results.BadRequest(new { error = "serviceCode is required." });

        // A suppressed mapping makes the suggester return empty — we surface
        // that explicitly so the UI can show "this code is suppressed; re-approve
        // to credit" instead of a confusing empty result.
        var existing = await repo.GetCrosswalkAsync(user.TenantId, serviceCode, ct);
        if (existing is { Status: 2 })
            return Results.Ok(new { serviceCode, suppressed = true, candidates = Array.Empty<CrosswalkSuggestion>() });

        var suggestions = await repo.SuggestCrosswalkAsync(
            user.TenantId, year, serviceCode, description,
            Math.Clamp(limit ?? 10, 1, 50), ct);

        return Results.Ok(new
        {
            serviceCode,
            suppressed = false,
            existing,           // the current approved mapping (if any) — UI can hint "already mapped to X"
            candidates = suggestions,
        });
    }

    [Authorize]
    private static async Task<IResult> CreateCrosswalkAsync(
        CrosswalkUpsertRequest req,
        ICurrentUser currentUser,
        IBillingRepository repo,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessBilling()) return Results.Forbid();

        var validation = ValidateUpsert(req);
        if (validation is not null) return validation;

        // Create-only: if a row already exists for (tenant, service_code), return
        // 409 with the current mapping so the UI can refetch and show
        // "another user just mapped this to X" rather than a generic error.
        var existing = await repo.GetCrosswalkAsync(user.TenantId, req.ServiceCode, ct);
        if (existing is not null)
            return Results.Conflict(new { error = "A mapping for that service_code already exists.", existing });

        var upsert = new ServiceCodeMappingUpsert(
            ServiceCode:            req.ServiceCode,
            CptCode:                req.CptCode,
            Source:                 req.Source ?? (short)1,           // default to manual
            Status:                 req.Status,
            Note:                   req.Note,
            ApprovedForDescription: req.ApprovedForDescription);

        CrosswalkUpsertResult result;
        try
        {
            result = await repo.UpsertCrosswalkAsync(user.TenantId, user.UserId, upsert, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Race: another writer beat us between the Get and the Upsert.
            var fresh = await repo.GetCrosswalkAsync(user.TenantId, req.ServiceCode, ct);
            return Results.Conflict(new { error = "A mapping for that service_code already exists.", existing = fresh });
        }

        await audit.WriteSuccessAsync(
            user.TenantId, user, AccessAction.Create,
            $"billing.service_code_crosswalk {result.Mapping.ServiceCode}→{result.Mapping.CptCode} " +
                $"(source={result.Mapping.Source} status={result.Mapping.Status})",
            http, ct);

        return Results.Ok(result.Mapping);
    }

    [Authorize]
    private static async Task<IResult> UpdateCrosswalkAsync(
        string serviceCode,
        CrosswalkUpsertRequest req,
        ICurrentUser currentUser,
        IBillingRepository repo,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessBilling()) return Results.Forbid();

        // Path param wins over body — defensive against a mismatched serviceCode.
        var corrected = req with { ServiceCode = serviceCode };
        var validation = ValidateUpsert(corrected);
        if (validation is not null) return validation;

        var before = await repo.GetCrosswalkAsync(user.TenantId, serviceCode, ct);
        if (before is null)
            return Results.NotFound(new { error = $"No crosswalk row for service_code='{serviceCode}'." });

        var upsert = new ServiceCodeMappingUpsert(
            ServiceCode:            corrected.ServiceCode,
            CptCode:                corrected.CptCode,
            Source:                 corrected.Source ?? before.Source,
            Status:                 corrected.Status ?? before.Status,
            Note:                   corrected.Note ?? before.Note,
            ApprovedForDescription: corrected.ApprovedForDescription);

        var result = await repo.UpsertCrosswalkAsync(user.TenantId, user.UserId, upsert, ct);

        await audit.WriteSuccessAsync(
            user.TenantId, user, AccessAction.Update,
            $"billing.service_code_crosswalk {result.Mapping.ServiceCode}: " +
                $"cpt {before.CptCode}→{result.Mapping.CptCode}, " +
                $"status {before.Status}→{result.Mapping.Status}",
            http, ct);

        return Results.Ok(result.Mapping);
    }

    [Authorize]
    private static async Task<IResult> BulkImportCrosswalkAsync(
        CrosswalkBulkImportRequest req,
        ICurrentUser currentUser,
        IBillingRepository repo,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessBilling()) return Results.Forbid();
        if (req?.Rows is null || req.Rows.Count == 0)
            return Results.BadRequest(new { error = "rows is required and must be non-empty." });
        if (req.Rows.Count > 5000)
            return Results.BadRequest(new { error = "rows cannot exceed 5000 per request." });
        if (req.OnConflict is not (null or "skip" or "update"))
            return Results.BadRequest(new { error = "onConflict must be 'skip' or 'update'." });

        // Reject obviously-bad rows up front so the bulk INSERT doesn't fail on
        // a single empty field. The repo normalizes case/whitespace.
        foreach (var (row, i) in req.Rows.Select((r, i) => (r, i)))
        {
            if (string.IsNullOrWhiteSpace(row.ServiceCode) || string.IsNullOrWhiteSpace(row.CptCode))
                return Results.BadRequest(new { error = $"Row {i} has an empty service_code or cpt_code." });
        }

        var updateOnConflict = req.OnConflict == "update";
        var result = await repo.BulkUpsertCrosswalkAsync(
            user.TenantId, user.UserId, req.Rows, updateOnConflict, ct);

        await audit.WriteSuccessAsync(
            user.TenantId, user, AccessAction.Create,
            $"billing.service_code_crosswalk bulk import: " +
                $"inserted={result.Inserted} updated={result.Updated} " +
                $"skipped={result.Skipped} errors={result.Errors} " +
                $"onConflict={req.OnConflict ?? "skip"}",
            http, ct);

        return Results.Ok(result);
    }

    private static IResult? ValidateUpsert(CrosswalkUpsertRequest req)
    {
        if (req is null)
            return Results.BadRequest(new { error = "Request body is required." });
        if (string.IsNullOrWhiteSpace(req.ServiceCode))
            return Results.BadRequest(new { error = "serviceCode is required." });
        if (string.IsNullOrWhiteSpace(req.CptCode))
            return Results.BadRequest(new { error = "cptCode is required." });
        if (req.Status is not null and not (1 or 2))
            return Results.BadRequest(new { error = "status must be 1 (approved) or 2 (suppressed)." });
        if (req.Source is not null and not (1 or 2 or 3))
            return Results.BadRequest(new { error = "source must be 1 (manual), 2 (suggested), or 3 (bulk)." });
        return null;
    }
}

public sealed record PatchCptCodeRequest(
    short Year,
    decimal? WorkRvu,
    string? Description,
    string? Notes);

public sealed record RunReconciliationRequest(
    DateTimeOffset From,
    DateTimeOffset To,
    string? Site,
    long? FacilityId);

public sealed record CrosswalkUpsertRequest(
    string ServiceCode,
    string CptCode,
    short? Source = null,
    short? Status = null,
    string? Note = null,
    string? ApprovedForDescription = null);

public sealed record CrosswalkBulkImportRequest(
    IReadOnlyList<BulkImportRow> Rows,
    string? OnConflict);
