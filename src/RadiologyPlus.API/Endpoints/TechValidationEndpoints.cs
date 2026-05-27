using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Npgsql;
using RadiologyPlus.API.Hubs;
using RadiologyPlus.Core.Identity;
using RadiologyPlus.Core.TechValidation;

namespace RadiologyPlus.API.Endpoints;

public static class TechValidationEndpoints
{
    public static IEndpointRouteBuilder MapTechValidationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/tech-validation")
            .WithTags("TechValidation")
            .RequireAuthorization();

        group.MapGet("/worklist", GetWorklistAsync).WithName("TechWorklist");
        group.MapPost("/start", StartValidationAsync).WithName("TechStart");
        group.MapGet("/{validationId:guid}", GetValidationAsync).WithName("TechGet");
        group.MapPost("/{validationId:guid}/step/{step:int}", UpdateStepAsync).WithName("TechUpdateStep");
        group.MapGet("/orders/by-patient/{patientPid}", GetCandidateOrdersAsync).WithName("TechCandidateOrders");
        group.MapGet("/comparisons/{novaradPatientId:long}", GetComparisonCandidatesAsync).WithName("TechComparisons");
        group.MapGet("/patients/search", GetPatientSearchAsync).WithName("TechPatientSearch");
        group.MapGet("/templates", GetTemplatesAsync).WithName("TechTemplates");
        group.MapPost("/templates", CreateTemplateAsync).WithName("TechTemplateCreate");
        group.MapPut("/templates/{templateId:long}", UpdateTemplateAsync).WithName("TechTemplateUpdate");
        group.MapDelete("/templates/{templateId:long}", DeleteTemplateAsync).WithName("TechTemplateDelete");
        group.MapPost("/{validationId:guid}/finalize", FinalizeAsync).WithName("TechFinalize");
        group.MapPost("/{validationId:guid}/cancel", CancelAsync).WithName("TechCancel");
        group.MapPost("/studies/merge", MergeStudiesAsync).WithName("TechStudyMerge");

        // Dev/ops helper: kick the projector once without running the Service worker.
        // NRS-only — this exists for smoke-testing and on-call diagnostics.
        group.MapPost("/dev/refresh", DevRefreshAsync).WithName("TechDevRefresh");

        return app;
    }

    [Authorize]
    private static async Task<IResult> DevRefreshAsync(
        ICurrentUser currentUser,
        INovaradStudyReader reader,
        ITechValidationRepository repo,
        [FromQuery] int? lookbackDays,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (user.Role != Role.NRS) return Results.Forbid();

        // Defaults to the projector's 7-day window; overridable for diagnostics
        // and for testing against datasets whose newest study predates the
        // 7-day window (e.g. a restored DB copy on an older snapshot).
        var window = TimeSpan.FromDays(Math.Clamp(lookbackDays ?? 7, 1, 3650));
        var studies = await reader.ReadReadyStudiesAsync(window, ct);
        if (studies.Count > 0)
        {
            await repo.UpsertReadyStudiesAsync(user.TenantId, studies, ct);
        }
        return Results.Ok(new { projected = studies.Count, lookbackDays = window.TotalDays });
    }

    [Authorize]
    private static async Task<IResult> GetWorklistAsync(
        ICurrentUser currentUser,
        ITechValidationRepository repo,
        [FromQuery] int? facility,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessTechValidation())
            return Results.Forbid();

        var facilities = facility.HasValue ? new[] { facility.Value } : (int[]?)null;
        var rows = await repo.GetWorklistAsync(user.TenantId, facilities, limit ?? 200, ct);
        return Results.Ok(rows);
    }

    [Authorize]
    private static async Task<IResult> StartValidationAsync(
        StartValidationRequest req,
        ICurrentUser currentUser,
        ITechValidationRepository repo,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessTechValidation())
            return Results.Forbid();

        // If an open validation already exists, hand it back instead of starting a new one.
        var existing = await repo.GetOpenValidationForStudyAsync(user.TenantId, req.NovaradStudyId, ct);
        if (existing is not null) return Results.Ok(existing);

        var validationId = await repo.StartValidationAsync(
            user.TenantId, req.NovaradStudyId, req.NovaradPatientId, user.UserId, ct);
        var rec = await repo.GetValidationAsync(user.TenantId, validationId, ct);
        return Results.Created($"/tech-validation/{validationId}", rec);
    }

    [Authorize]
    private static async Task<IResult> GetValidationAsync(
        Guid validationId, ICurrentUser currentUser, ITechValidationRepository repo, CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessTechValidation()) return Results.Forbid();
        var rec = await repo.GetValidationAsync(user.TenantId, validationId, ct);
        return rec is null ? Results.NotFound() : Results.Ok(rec);
    }

    [Authorize]
    private static async Task<IResult> UpdateStepAsync(
        Guid validationId,
        int step,
        WizardStepRequest req,
        ICurrentUser currentUser,
        ITechValidationRepository repo,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessTechValidation()) return Results.Forbid();
        if (step is < 1 or > 5) return Results.BadRequest(new { error = "step must be 1..5" });

        await repo.UpdateStepAsync(
            user.TenantId, validationId, (WizardStep)step,
            new ValidationStepUpdate(
                NovaradOrderId: req.NovaradOrderId,
                NovaradPatientId: req.NovaradPatientId,
                Reason: req.Reason,
                TechNotes: req.TechNotes,
                ReferringPhysicianId: req.ReferringPhysicianId,
                ComparisonStudyIds: req.ComparisonStudyIds,
                CreateOrderRequested: req.CreateOrderRequested,
                PatientAction: req.PatientAction.HasValue ? (PatientAction)req.PatientAction.Value : null,
                Correction: req.PatientCorrection is null
                    ? null
                    : new PatientCorrection(
                        req.PatientCorrection.LastName,
                        req.PatientCorrection.FirstName,
                        req.PatientCorrection.MiddleName,
                        req.PatientCorrection.BirthDate,
                        req.PatientCorrection.Gender),
                ReassignTargetPatientId: req.ReassignTargetPatientId),
            ct);

        var rec = await repo.GetValidationAsync(user.TenantId, validationId, ct);
        return rec is null ? Results.NotFound() : Results.Ok(rec);
    }

    [Authorize]
    private static async Task<IResult> GetCandidateOrdersAsync(
        string patientPid, ICurrentUser currentUser, INovaradStudyReader reader, CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessTechValidation()) return Results.Forbid();
        var rows = await reader.ReadCandidateOrdersForPatientAsync(patientPid, ct);
        return Results.Ok(rows);
    }

    [Authorize]
    private static async Task<IResult> GetComparisonCandidatesAsync(
        long novaradPatientId,
        [FromQuery] string? lastName,
        [FromQuery] int? limit,
        ICurrentUser currentUser,
        INovaradStudyReader reader,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessTechValidation()) return Results.Forbid();
        var rows = await reader.ReadComparisonCandidatesAsync(novaradPatientId, lastName, limit ?? 25, ct);
        return Results.Ok(rows);
    }

    [Authorize]
    private static async Task<IResult> GetPatientSearchAsync(
        [FromQuery] string? q,
        [FromQuery] int? limit,
        ICurrentUser currentUser,
        INovaradStudyReader reader,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessTechValidation()) return Results.Forbid();

        var query = (q ?? string.Empty).Trim();
        if (query.Length < 2)
            return Results.Ok(Array.Empty<PatientSearchResult>());

        var rows = await reader.SearchPatientsAsync(query, Math.Clamp(limit ?? 20, 1, 100), ct);
        return Results.Ok(rows);
    }

    [Authorize]
    private static async Task<IResult> GetTemplatesAsync(
        ICurrentUser currentUser, ITechValidationRepository repo, CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessTechValidation()) return Results.Forbid();
        var rows = await repo.ListActiveTemplatesAsync(user.TenantId, ct);
        return Results.Ok(rows);
    }

    [Authorize]
    private static async Task<IResult> CreateTemplateAsync(
        TemplateUpsertRequest req,
        ICurrentUser currentUser,
        ITechValidationRepository repo,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (user.Role is not (Role.NRS or Role.Admin)) return Results.Forbid();
        if (string.IsNullOrWhiteSpace(req.Label) || string.IsNullOrWhiteSpace(req.Body))
            return Results.BadRequest(new { error = "label and body are required" });

        try
        {
            var created = await repo.CreateTemplateAsync(
                user.TenantId, user.UserId, req.Label.Trim(), req.Body.Trim(), req.SortOrder ?? 0, ct);
            return Results.Created($"/tech-validation/templates/{created.TemplateId}", created);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Results.Conflict(new { error = "A template with that label already exists." });
        }
    }

    [Authorize]
    private static async Task<IResult> UpdateTemplateAsync(
        long templateId,
        TemplateUpsertRequest req,
        ICurrentUser currentUser,
        ITechValidationRepository repo,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (user.Role is not (Role.NRS or Role.Admin)) return Results.Forbid();
        if (string.IsNullOrWhiteSpace(req.Label) || string.IsNullOrWhiteSpace(req.Body))
            return Results.BadRequest(new { error = "label and body are required" });

        try
        {
            var updated = await repo.UpdateTemplateAsync(
                user.TenantId, templateId, req.Label.Trim(), req.Body.Trim(), req.SortOrder ?? 0, ct);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Results.Conflict(new { error = "A template with that label already exists." });
        }
    }

    [Authorize]
    private static async Task<IResult> DeleteTemplateAsync(
        long templateId,
        ICurrentUser currentUser,
        ITechValidationRepository repo,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (user.Role is not (Role.NRS or Role.Admin)) return Results.Forbid();
        var ok = await repo.DeactivateTemplateAsync(user.TenantId, templateId, ct);
        return ok ? Results.NoContent() : Results.NotFound();
    }

    [Authorize]
    private static async Task<IResult> FinalizeAsync(
        Guid validationId,
        ICurrentUser currentUser,
        IDoTheDoOrchestrator orchestrator,
        IHubContext<MonitoringHub> hub,
        ILogger<MonitoringHub> logger,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessTechValidation()) return Results.Forbid();

        var progress = new Progress<DoTheDoProgressEvent>(async ev =>
        {
            try
            {
                await hub.Clients
                    .Group(MonitoringHub.ValidationGroup(validationId))
                    .SendAsync("DoTheDoProgress", ev, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to broadcast DoTheDoProgress for {Validation}.", validationId);
            }
        });

        var outcome = await orchestrator.RunAsync(validationId, progress, ct);
        return outcome.Success
            ? Results.Ok(outcome)
            : Results.UnprocessableEntity(outcome);
    }

    [Authorize]
    private static async Task<IResult> CancelAsync(
        Guid validationId, ICurrentUser currentUser, ITechValidationRepository repo, CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessTechValidation()) return Results.Forbid();
        await repo.SetStatusAsync(user.TenantId, validationId, ValidationStatus.Cancelled, ct);
        return Results.NoContent();
    }

    [Authorize]
    private static async Task<IResult> MergeStudiesAsync(
        StudyMergeRequestDto req,
        ICurrentUser currentUser,
        IStudyMergeService service,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessTechValidation()) return Results.Forbid();
        if (req is null)
            return Results.BadRequest(new { error = "Request body is required." });
        if (req.LosingStudyIds is null || req.LosingStudyIds.Count == 0)
            return Results.BadRequest(new { error = "losingStudyIds must be non-empty." });

        var outcome = await service.MergeStudiesAsync(
            new StudyMergeRequest(req.WinningStudyId, req.LosingStudyIds, req.Reason), ct);

        // Pre-flight rejection → 422 so the UI can surface the rule that broke.
        // Per-row merge failures inside the loop still return 200 with a partial
        // outcome; the UI shows per-row success/error.
        return outcome.PreflightError is not null
            ? Results.UnprocessableEntity(outcome)
            : Results.Ok(outcome);
    }
}

public sealed record StartValidationRequest(long NovaradStudyId, long NovaradPatientId);

public sealed record WizardStepRequest(
    long? NovaradOrderId,
    long? NovaradPatientId,
    string? Reason,
    string? TechNotes,
    long? ReferringPhysicianId,
    long[]? ComparisonStudyIds,
    bool? CreateOrderRequested,
    short? PatientAction = null,
    PatientCorrectionRequest? PatientCorrection = null,
    long? ReassignTargetPatientId = null);

public sealed record PatientCorrectionRequest(
    string? LastName,
    string? FirstName,
    string? MiddleName,
    DateOnly? BirthDate,
    string? Gender);

public sealed record TemplateUpsertRequest(string Label, string Body, int? SortOrder);

public sealed record StudyMergeRequestDto(
    long WinningStudyId,
    IReadOnlyList<long> LosingStudyIds,
    string? Reason);
