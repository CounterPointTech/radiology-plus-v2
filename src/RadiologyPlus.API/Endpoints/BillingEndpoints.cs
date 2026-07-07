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

    // CMS RVU zips are larger — RVU26A is ~5.7 MB; the bare PPRRVU csv ~2.6 MB.
    // Cap at 25 MB (still under Kestrel's default request-body limit).
    private const long MaxRvuUploadBytes = 25 * 1024 * 1024;

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

        // Item 1.2 — CMS RVU source-of-truth (billing.rvu_values)
        group.MapPost("/rvu/import", ImportRvuValuesAsync)
             .WithName("BillingImportRvu")
             .DisableAntiforgery();                      // bearer-token auth + same-origin CORS is our guard
        group.MapGet("/rvu", ListRvuValuesAsync).WithName("BillingListRvu");
        group.MapGet("/rvu/imports", ListRvuImportsAsync).WithName("BillingListRvuImports");
        group.MapGet("/rvu/overrides", ListRvuOverridesAsync).WithName("BillingListRvuOverrides");
        group.MapPut("/rvu/overrides/{code}", UpsertRvuOverrideAsync).WithName("BillingUpsertRvuOverride");
        group.MapDelete("/rvu/overrides/{code}", DeleteRvuOverrideAsync).WithName("BillingDeleteRvuOverride");
        group.MapGet("/cpt-master/cms-check", CptMasterCmsCheckAsync).WithName("BillingCptMasterCmsCheck");

        // M*Modal RVU write-back (project-ffi-rvu-writeback)
        group.MapGet("/rvu/sync/status", RvuSyncStatusAsync).WithName("BillingRvuSyncStatus");
        group.MapGet("/rvu/sync/issuers", ListRvuSyncIssuersAsync).WithName("BillingRvuSyncIssuers");
        group.MapGet("/rvu/sync/runs", ListRvuSyncRunsAsync).WithName("BillingRvuSyncRuns");
        group.MapPost("/rvu/sync/preview", PreviewRvuSyncAsync).WithName("BillingRvuSyncPreview");
        group.MapPost("/rvu/sync", ApplyRvuSyncAsync).WithName("BillingRvuSyncApply");
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
        group.MapGet("/sites", ListSitesAsync).WithName("BillingListSites");

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
    // Item 1.2 — CMS RVU source-of-truth (billing.rvu_values)
    // ========================================================================

    [Authorize]
    private static async Task<IResult> ImportRvuValuesAsync(
        HttpRequest request,
        ICurrentUser currentUser,
        IBillingRepository repo,
        IRvuValuesImporter importer,
        [FromQuery] short? year,
        [FromQuery] string? quarter,
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
        if (file.Length > MaxRvuUploadBytes)
            return Results.BadRequest(new { error = $"File exceeds the {MaxRvuUploadBytes / 1024 / 1024} MB upload cap." });

        var resolvedYear = year ?? (short)DateTime.Now.Year;
        var q = string.IsNullOrWhiteSpace(quarter) ? 'A' : char.ToUpperInvariant(quarter.Trim()[0]);
        if (q is not ('A' or 'B' or 'C' or 'D'))
            return Results.BadRequest(new { error = "quarter must be one of A, B, C, D (A=Jan, B=Apr, C=Jul, D=Oct)." });

        await using var stream = file.OpenReadStream();
        var parsed = importer.Parse(stream, file.FileName, resolvedYear, q);

        // Always persist an import header — even a 0-row/garbage upload leaves an audited
        // billing.rvu_imports row carrying the parse errors, matching the CPT-master contract.
        var header = await repo.ImportRvuValuesAsync(
            user.TenantId, user.UserId, file.FileName,
            parsed.Year, parsed.Quarter, parsed.Rows, parsed.Errors, ct);

        return Results.Ok(header);
    }

    [Authorize]
    private static async Task<IResult> ListRvuValuesAsync(
        ICurrentUser currentUser,
        IBillingRepository repo,
        [FromQuery] short? year,
        [FromQuery] string? quarter,
        [FromQuery] string? q,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessBilling()) return Results.Forbid();

        char? quarterChar = string.IsNullOrWhiteSpace(quarter)
            ? null
            : char.ToUpperInvariant(quarter.Trim()[0]);

        var rows = await repo.ListRvuValuesAsync(
            user.TenantId, year, quarterChar, q,
            Math.Clamp(limit ?? 100, 1, 1000), ct);
        return Results.Ok(rows);
    }

    [Authorize]
    private static async Task<IResult> ListRvuImportsAsync(
        ICurrentUser currentUser,
        IBillingRepository repo,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessBilling()) return Results.Forbid();

        var rows = await repo.ListRecentRvuImportsAsync(
            user.TenantId, Math.Clamp(limit ?? 25, 1, 200), ct);
        return Results.Ok(rows);
    }

    // ── M*Modal RVU write-back (project-ffi-rvu-writeback) ──────────────────

    [Authorize]
    private static async Task<IResult> RvuSyncStatusAsync(
        ICurrentUser currentUser,
        IBillingRepository repo,
        IRvuWriteBackSink sink,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessBilling()) return Results.Forbid();

        var configured = await sink.IsConfiguredAsync(user.TenantId, ct);
        var lastRuns = await repo.ListRecentSyncRunsAsync(user.TenantId, 1, ct);
        return Results.Ok(new { configured, lastRun = lastRuns.Count > 0 ? lastRuns[0] : null });
    }

    [Authorize]
    private static async Task<IResult> ListRvuSyncIssuersAsync(
        ICurrentUser currentUser,
        IRvuWriteBackSink sink,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessBilling()) return Results.Forbid();

        var issuers = await sink.ListIssuersAsync(user.TenantId, ct);
        return Results.Ok(issuers);
    }

    [Authorize]
    private static async Task<IResult> ListRvuSyncRunsAsync(
        ICurrentUser currentUser,
        IBillingRepository repo,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessBilling()) return Results.Forbid();

        var rows = await repo.ListRecentSyncRunsAsync(
            user.TenantId, Math.Clamp(limit ?? 10, 1, 100), ct);
        return Results.Ok(rows);
    }

    [Authorize]
    private static async Task<IResult> PreviewRvuSyncAsync(
        ICurrentUser currentUser,
        IBillingRepository repo,
        IRvuWriteBackSink sink,
        [FromQuery] short? year,
        [FromQuery] string? quarter,
        [FromQuery] string? issuerKey,
        [FromQuery] bool? allIssuers,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessBilling()) return Results.Forbid();

        var resolvedYear = year ?? (short)DateTime.Now.Year;
        var q = ResolveQuarter(quarter, out var qErr);
        if (qErr is not null) return Results.BadRequest(new { error = qErr });
        if (!TryResolveIssuerScope(issuerKey, allIssuers, out var scope, out var scopeErr))
            return Results.BadRequest(new { error = scopeErr });

        var desired = await repo.GetEffectiveWorkRvusAsync(user.TenantId, resolvedYear, ct);
        var preview = await sink.PreviewAsync(user.TenantId, resolvedYear, q, scope, desired, ct);
        return Results.Ok(preview);
    }

    [Authorize]
    private static async Task<IResult> ApplyRvuSyncAsync(
        ICurrentUser currentUser,
        IBillingRepository repo,
        IRvuWriteBackSink sink,
        [FromQuery] short? year,
        [FromQuery] string? quarter,
        [FromQuery] string? issuerKey,
        [FromQuery] bool? allIssuers,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessBilling()) return Results.Forbid();

        var resolvedYear = year ?? (short)DateTime.Now.Year;
        var q = ResolveQuarter(quarter, out var qErr);
        if (qErr is not null) return Results.BadRequest(new { error = qErr });
        if (!TryResolveIssuerScope(issuerKey, allIssuers, out var scope, out var scopeErr))
            return Results.BadRequest(new { error = scopeErr });

        var desired = await repo.GetEffectiveWorkRvusAsync(user.TenantId, resolvedYear, ct);
        var result = await sink.ApplyAsync(user.TenantId, resolvedYear, q, scope, desired, user.UserId, user.Username, ct);

        if (!result.Configured)
            return Results.BadRequest(new { error = "M*Modal write-back is not configured for this tenant." });

        // Persist an audited run header for the (configured) attempt — success or failure.
        await repo.RecordSyncRunAsync(
            user.TenantId, user.UserId, resolvedYear, q, scope, dryRun: false,
            result.Matched, result.Updated, result.Unchanged, result.Missing,
            result.Success, result.Error, ct);

        return Results.Ok(result);
    }

    // A/B/C/D quarter parse shared by the sync handlers; defaults to 'A'.
    private static char ResolveQuarter(string? quarter, out string? error)
    {
        error = null;
        var q = string.IsNullOrWhiteSpace(quarter) ? 'A' : char.ToUpperInvariant(quarter.Trim()[0]);
        if (q is not ('A' or 'B' or 'C' or 'D'))
            error = "quarter must be one of A, B, C, D (A=Jan, B=Apr, C=Jul, D=Oct).";
        return q;
    }

    // Resolve the M*Modal issuer scope: exactly one of a specific issuerKey (one facility) or
    // allIssuers=true (every facility — the warned power option). Neither is rejected so a
    // caller can never accidentally hit all facilities by omitting the scope.
    private static bool TryResolveIssuerScope(string? issuerKey, bool? allIssuers, out Guid? scope, out string? error)
    {
        scope = null;
        error = null;
        var all = allIssuers == true;
        var hasKey = !string.IsNullOrWhiteSpace(issuerKey);

        if (all && hasKey)
        {
            error = "Pass either issuerKey (one facility) or allIssuers=true, not both.";
            return false;
        }
        if (all) return true;                 // scope stays null = all issuers
        if (!hasKey)
        {
            error = "Specify issuerKey (one facility) or allIssuers=true.";
            return false;
        }
        if (!Guid.TryParse(issuerKey, out var key))
        {
            error = "issuerKey must be a GUID.";
            return false;
        }
        scope = key;
        return true;
    }

    [Authorize]
    private static async Task<IResult> ListRvuOverridesAsync(
        ICurrentUser currentUser,
        IBillingRepository repo,
        [FromQuery] short? year,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessBilling()) return Results.Forbid();

        var rows = await repo.ListRvuOverridesAsync(user.TenantId, year, ct);
        return Results.Ok(rows);
    }

    [Authorize]
    private static async Task<IResult> UpsertRvuOverrideAsync(
        string code,
        RvuOverrideRequest req,
        ICurrentUser currentUser,
        IBillingRepository repo,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessBilling()) return Results.Forbid();

        if (req is null) return Results.BadRequest(new { error = "Request body is required." });
        if (string.IsNullOrWhiteSpace(code)) return Results.BadRequest(new { error = "code is required." });
        if (req.Year is < 2000 or > 2100)
            return Results.BadRequest(new { error = "year must be between 2000 and 2100." });
        if (req.OverrideWorkRvu < 0)
            return Results.BadRequest(new { error = "overrideWorkRvu must be non-negative." });
        // numeric(8,4): max magnitude is 9999.9999. Reject out-of-range up front so the
        // INSERT doesn't throw a numeric overflow mid-request.
        if (req.OverrideWorkRvu > 9999.9999m)
            return Results.BadRequest(new { error = "overrideWorkRvu exceeds the numeric(8,4) maximum (9999.9999)." });
        if (req.SiteCode is not null && string.IsNullOrWhiteSpace(req.SiteCode))
            return Results.BadRequest(new { error = "siteCode must be a non-empty site, or omitted for tenant-wide." });

        var upsert = new RvuOverrideUpsert(req.Year, code, req.OverrideWorkRvu, req.Note, req.SiteCode);
        var result = await repo.UpsertRvuOverrideAsync(user.TenantId, user.UserId, upsert, ct);

        var scope = result.Override.SiteCode is null ? "tenant-wide" : $"site {result.Override.SiteCode}";
        await audit.WriteSuccessAsync(
            user.TenantId, user, result.Inserted ? AccessAction.Create : AccessAction.Update,
            $"billing.rvu_overrides {result.Override.Year}/{result.Override.Code} " +
                $"= {result.Override.OverrideWorkRvu} work RVU ({scope})",
            http, ct);

        return Results.Ok(result.Override);
    }

    [Authorize]
    private static async Task<IResult> DeleteRvuOverrideAsync(
        string code,
        ICurrentUser currentUser,
        IBillingRepository repo,
        IAccessAuditWriter audit,
        HttpContext http,
        [FromQuery] short? year,
        [FromQuery] string? siteCode,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessBilling()) return Results.Forbid();
        if (year is null)
            return Results.BadRequest(new { error = "year query parameter is required." });
        if (siteCode is not null && string.IsNullOrWhiteSpace(siteCode))
            return Results.BadRequest(new { error = "siteCode must be a non-empty site, or omitted for tenant-wide." });

        var removed = await repo.DeleteRvuOverrideAsync(user.TenantId, year.Value, code, siteCode, ct);
        var scope = siteCode is null ? "tenant-wide" : $"site {siteCode}";
        if (!removed)
            return Results.NotFound(new { error = $"No {scope} override for {year}/{code}." });

        await audit.WriteSuccessAsync(
            user.TenantId, user, AccessAction.Delete,
            $"billing.rvu_overrides {year}/{code} removed ({scope})",
            http, ct);

        return Results.NoContent();
    }

    [Authorize]
    private static async Task<IResult> ListSitesAsync(
        ICurrentUser currentUser,
        INovaradReportsReader reader,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessBilling()) return Results.Forbid();

        var sites = await reader.ReadAllSiteCodesAsync(ct);
        return Results.Ok(sites);
    }

    [Authorize]
    private static async Task<IResult> CptMasterCmsCheckAsync(
        ICurrentUser currentUser,
        IBillingRepository repo,
        [FromQuery] short? year,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessBilling()) return Results.Forbid();

        var rows = await repo.ListCptMasterCmsAsync(
            user.TenantId,
            year ?? (short)DateTime.Now.Year,
            Math.Clamp(limit ?? 2000, 1, 5000),
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
        // to credit" instead of a confusing empty result. Suggestions are
        // site-independent, so this checks the tenant-wide default row.
        var existing = await repo.GetCrosswalkAsync(user.TenantId, serviceCode, siteCode: null, ct);
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

        // Create-only: if a row already exists for (tenant, service_code, site),
        // return 409 with the current mapping so the UI can refetch and show
        // "another user just mapped this to X" rather than a generic error. The
        // site scope matters — a site row must not trip on the tenant default.
        var existing = await repo.GetCrosswalkAsync(user.TenantId, req.ServiceCode, req.SiteCode, ct);
        if (existing is not null)
            return Results.Conflict(new { error = "A mapping for that service_code already exists.", existing });

        var upsert = new ServiceCodeMappingUpsert(
            ServiceCode:            req.ServiceCode,
            CptCode:                req.CptCode,
            Source:                 req.Source ?? (short)1,           // default to manual
            Status:                 req.Status,
            Note:                   req.Note,
            ApprovedForDescription: req.ApprovedForDescription,
            SiteCode:               req.SiteCode);

        CrosswalkUpsertResult result;
        try
        {
            result = await repo.UpsertCrosswalkAsync(user.TenantId, user.UserId, upsert, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Race: another writer beat us between the Get and the Upsert.
            var fresh = await repo.GetCrosswalkAsync(user.TenantId, req.ServiceCode, req.SiteCode, ct);
            return Results.Conflict(new { error = "A mapping for that service_code already exists.", existing = fresh });
        }

        await audit.WriteSuccessAsync(
            user.TenantId, user, AccessAction.Create,
            $"billing.service_code_crosswalk {result.Mapping.ServiceCode}→{result.Mapping.CptCode} " +
                $"(source={result.Mapping.Source} status={result.Mapping.Status} " +
                $"scope={result.Mapping.SiteCode ?? "tenant-wide"})",
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

        var before = await repo.GetCrosswalkAsync(user.TenantId, serviceCode, corrected.SiteCode, ct);
        if (before is null)
            return Results.NotFound(new { error = $"No crosswalk row for service_code='{serviceCode}'." });

        var upsert = new ServiceCodeMappingUpsert(
            ServiceCode:            corrected.ServiceCode,
            CptCode:                corrected.CptCode,
            Source:                 corrected.Source ?? before.Source,
            Status:                 corrected.Status ?? before.Status,
            Note:                   corrected.Note ?? before.Note,
            ApprovedForDescription: corrected.ApprovedForDescription,
            SiteCode:               corrected.SiteCode);

        var result = await repo.UpsertCrosswalkAsync(user.TenantId, user.UserId, upsert, ct);

        await audit.WriteSuccessAsync(
            user.TenantId, user, AccessAction.Update,
            $"billing.service_code_crosswalk {result.Mapping.ServiceCode} " +
                $"(scope={result.Mapping.SiteCode ?? "tenant-wide"}): " +
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
        if (req.SiteCode is not null && string.IsNullOrWhiteSpace(req.SiteCode))
            return Results.BadRequest(new { error = "siteCode must be a non-empty site, or omitted for tenant-wide." });
        return null;
    }
}

public sealed record PatchCptCodeRequest(
    short Year,
    decimal? WorkRvu,
    string? Description,
    string? Notes);

public sealed record RvuOverrideRequest(
    short Year,
    decimal OverrideWorkRvu,
    string? Note,
    string? SiteCode = null);                           // null = tenant-wide; set = site-specific

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
    string? ApprovedForDescription = null,
    string? SiteCode = null);                          // null = tenant-wide default; set = site-specific

public sealed record CrosswalkBulkImportRequest(
    IReadOnlyList<BulkImportRow> Rows,
    string? OnConflict);
