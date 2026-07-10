using Microsoft.AspNetCore.Mvc;
using Npgsql;
using RadiologyPlus.Core.Audit;
using RadiologyPlus.Core.Identity;
using RadiologyPlus.Notifications;
using RadiologyPlus.Scripting;

namespace RadiologyPlus.AdminApi.Endpoints;

/// <summary>
/// Script Chains surface (NRS only, like the Script Manager; every mutation audited).
/// Run-now creates the run row synchronously and returns its id (202) — the steps
/// execute inside the ChainRunner's background task and the UI polls the run detail.
/// </summary>
public static class ChainsEndpoints
{
    public static IEndpointRouteBuilder MapChainsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/chains")
            .WithTags("Chains")
            .RequireAuthorization();

        group.MapGet("/", ListChainsAsync).WithName("ChainsList");
        group.MapPost("/", CreateChainAsync).WithName("ChainsCreate");
        group.MapGet("/runs", ListAllRunsAsync).WithName("ChainsRunsRecent");
        group.MapGet("/runs/{chainRunId:long}", GetRunAsync).WithName("ChainsRunDetail");
        group.MapPost("/runs/{chainRunId:long}/cancel", CancelRunAsync).WithName("ChainsRunCancel");
        group.MapGet("/{chainId:guid}", GetChainAsync).WithName("ChainsGet");
        group.MapPut("/{chainId:guid}", UpdateChainAsync).WithName("ChainsUpdate");
        group.MapDelete("/{chainId:guid}", DeleteChainAsync).WithName("ChainsDelete");
        group.MapPatch("/{chainId:guid}/active", SetActiveAsync).WithName("ChainsSetActive");
        group.MapPost("/{chainId:guid}/run", RunChainAsync).WithName("ChainsRun");
        group.MapGet("/{chainId:guid}/runs", ListChainRunsAsync).WithName("ChainsRunsForChain");

        return app;
    }

    // -- List / read ---------------------------------------------------------

    private static async Task<IResult> ListChainsAsync(
        ICurrentUser currentUser, IChainAdminRepository repo, CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessScripting()) return Results.Forbid();

        var now = DateTimeOffset.UtcNow;
        var rows = await repo.ListAllAsync(user.TenantId, ct);
        return Results.Ok(rows.Select(c => new ChainSummaryDto(
            c.ChainId, c.Name, c.Description, c.OnFailure, c.CronExpression,
            CronHelper.NextOccurrenceUtc(c.IsActive ? c.CronExpression : null, now),
            c.IsActive, c.StepCount, c.NotifiesOnFailure, c.CreatedAt,
            c.LastRunId, c.LastRunStatus, c.LastRunStartedAt, c.LastRunDurationMs)).ToList());
    }

    private static async Task<IResult> GetChainAsync(
        Guid chainId, ICurrentUser currentUser, IChainAdminRepository repo, CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessScripting()) return Results.Forbid();

        var chain = await repo.GetDetailAsync(user.TenantId, chainId, ct);
        return chain is null
            ? Results.NotFound(new { error = "Chain not found." })
            : Results.Ok(ToDetailDto(chain));
    }

    // -- Create / update / delete / activate ----------------------------------

    private static async Task<IResult> CreateChainAsync(
        ChainSaveRequest req,
        ICurrentUser currentUser,
        IChainAdminRepository repo,
        INotificationAdminRepository templates,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessScripting()) return Results.Forbid();
        if (await ValidateAsync(req, user.TenantId, templates, ct) is var (bad, input) && bad is not null) return bad;

        try
        {
            var created = await repo.CreateAsync(user.TenantId, input!, ct);
            await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Create,
                $"scripting.script_chains chain_id={created.ChainId}: created '{created.Name}' ({created.Steps.Count} steps, on_failure={created.OnFailure})",
                http, ct);
            return Results.Ok(ToDetailDto(created));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Results.Conflict(new { error = $"A chain named \"{req.Name}\" already exists." });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> UpdateChainAsync(
        Guid chainId,
        ChainSaveRequest req,
        ICurrentUser currentUser,
        IChainAdminRepository repo,
        INotificationAdminRepository templates,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessScripting()) return Results.Forbid();
        if (await ValidateAsync(req, user.TenantId, templates, ct) is var (bad, input) && bad is not null) return bad;

        try
        {
            var updated = await repo.UpdateAsync(user.TenantId, chainId, input!, ct);
            await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Update,
                $"scripting.script_chains chain_id={chainId}: updated '{updated.Name}' ({updated.Steps.Count} steps)",
                http, ct);
            return Results.Ok(ToDetailDto(updated));
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { error = "Chain not found." });
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Results.Conflict(new { error = $"A chain named \"{req.Name}\" already exists." });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> SetActiveAsync(
        Guid chainId,
        ChainActiveRequest req,
        ICurrentUser currentUser,
        IChainAdminRepository repo,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessScripting()) return Results.Forbid();

        try
        {
            var updated = await repo.SetActiveAsync(user.TenantId, chainId, req.IsActive, ct);
            await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Update,
                $"scripting.script_chains chain_id={chainId}: active->{req.IsActive}", http, ct);
            return Results.Ok(ToDetailDto(updated));
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { error = "Chain not found." });
        }
    }

    private static async Task<IResult> DeleteChainAsync(
        Guid chainId,
        ICurrentUser currentUser,
        IChainAdminRepository repo,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessScripting()) return Results.Forbid();

        var removed = await repo.DeleteAsync(user.TenantId, chainId, ct);
        if (!removed) return Results.NotFound(new { error = "Chain not found." });

        await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Delete,
            $"scripting.script_chains chain_id={chainId} removed (run history kept on executions)", http, ct);
        return Results.NoContent();
    }

    // -- Run / runs -------------------------------------------------------------

    private static async Task<IResult> RunChainAsync(
        Guid chainId,
        ICurrentUser currentUser,
        IChainAdminRepository repo,
        ChainRunner runner,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessScripting()) return Results.Forbid();

        // Tenant-scoped existence check before handing off to the (unscoped) runner.
        var chain = await repo.GetDetailAsync(user.TenantId, chainId, ct);
        if (chain is null) return Results.NotFound(new { error = "Chain not found." });
        if (!chain.IsActive) return Results.BadRequest(new { error = "Chain is inactive — activate it before running." });
        if (chain.Steps.Count == 0) return Results.BadRequest(new { error = "Chain has no steps — add at least one script." });

        long runId;
        try
        {
            runId = await runner.StartAsync(chainId, "manual", user.UserId, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Execute,
            $"scripting.chain_runs chain_run_id={runId}: manual run of chain '{chain.Name}' started", http, ct);

        return Results.Accepted(value: new { started = true, chainRunId = runId });
    }

    private static async Task<IResult> ListAllRunsAsync(
        [FromQuery] int? limit, ICurrentUser currentUser, IChainAdminRepository repo, CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessScripting()) return Results.Forbid();
        var rows = await repo.ListRunsAsync(user.TenantId, null, Math.Clamp(limit ?? 50, 1, 200), ct);
        return Results.Ok(rows);
    }

    private static async Task<IResult> ListChainRunsAsync(
        Guid chainId, [FromQuery] int? limit, ICurrentUser currentUser, IChainAdminRepository repo, CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessScripting()) return Results.Forbid();
        var rows = await repo.ListRunsAsync(user.TenantId, chainId, Math.Clamp(limit ?? 50, 1, 200), ct);
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetRunAsync(
        long chainRunId, ICurrentUser currentUser, IChainAdminRepository repo, CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessScripting()) return Results.Forbid();
        var run = await repo.GetRunAsync(user.TenantId, chainRunId, ct);
        return run is null ? Results.NotFound(new { error = "Chain run not found." }) : Results.Ok(run);
    }

    private static async Task<IResult> CancelRunAsync(
        long chainRunId,
        ICurrentUser currentUser,
        IChainAdminRepository repo,
        ChainRunner runner,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessScripting()) return Results.Forbid();

        var run = await repo.GetRunAsync(user.TenantId, chainRunId, ct);
        if (run is null) return Results.NotFound(new { error = "Chain run not found." });

        // Only runs owned by THIS host's runner can be cancelled (manual runs).
        // Scheduled runs execute inside RadiologyPlus.AdminService.
        var cancelled = runner.TryCancel(chainRunId);
        if (cancelled)
        {
            await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Execute,
                $"scripting.chain_runs chain_run_id={chainRunId}: cancel requested", http, ct);
        }

        return Results.Ok(new
        {
            cancelled,
            message = cancelled
                ? "Cancellation requested — the current step is stopping; remaining steps are skipped."
                : "This run isn't cancellable from here — it may have finished, or it's running in the scheduler service.",
        });
    }

    // -- Validation + mapping ----------------------------------------------------

    private static async Task<(IResult? Bad, ChainSave? Input)> ValidateAsync(
        ChainSaveRequest req, Guid tenantId, INotificationAdminRepository templates, CancellationToken ct)
    {
        if (req is null)
            return (Results.BadRequest(new { error = "Request body is required." }), null);
        if (string.IsNullOrWhiteSpace(req.Name))
            return (Results.BadRequest(new { error = "name is required." }), null);
        if (req.Steps is null || req.Steps.Count == 0)
            return (Results.BadRequest(new { error = "A chain needs at least one step." }), null);

        // 'branch' is in the 0001 CHECK but has no schema support — not offered.
        var onFailure = string.IsNullOrWhiteSpace(req.OnFailure) ? "stop" : req.OnFailure.Trim().ToLowerInvariant();
        if (onFailure is not ("stop" or "continue"))
            return (Results.BadRequest(new { error = "onFailure must be stop or continue." }), null);

        var cron = string.IsNullOrWhiteSpace(req.CronExpression) ? null : req.CronExpression.Trim();
        if (cron is not null && !CronHelper.TryParse(cron, out _))
            return (Results.BadRequest(new { error = $"\"{cron}\" is not a valid cron expression." }), null);

        var recipient = string.IsNullOrWhiteSpace(req.NotifyOnFailureRecipient)
            ? null
            : req.NotifyOnFailureRecipient.Trim();
        if (req.NotifyOnFailureTemplateId is not null && recipient is null)
            return (Results.BadRequest(new { error = "A failure-notification template needs a recipient." }), null);
        if (req.NotifyOnFailureTemplateId is { } templateId)
        {
            var template = await templates.GetTemplateAsync(tenantId, templateId, ct);
            if (template is null)
                return (Results.BadRequest(new { error = "The failure-notification template doesn't exist." }), null);
            if (template.Channel != "email")
                return (Results.BadRequest(new { error = "The failure-notification template must be an email template." }), null);
        }

        var steps = req.Steps
            .Select(s => new ChainStepSave(s.ScriptId, s.ContinueOnFailure))
            .ToList();

        return (null, new ChainSave(
            req.Name.Trim(),
            string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim(),
            onFailure, cron, req.IsActive, recipient, req.NotifyOnFailureTemplateId, steps));
    }

    private static ChainDetailDto ToDetailDto(ChainDetail c) => new(
        c.ChainId, c.Name, c.Description, c.OnFailure, c.CronExpression,
        CronHelper.NextOccurrenceUtc(c.IsActive ? c.CronExpression : null, DateTimeOffset.UtcNow),
        c.IsActive, c.NotifyOnFailureRecipient, c.NotifyOnFailureTemplateId, c.CreatedAt,
        c.Steps.Select(s => new ChainStepDto(
            s.StepOrder, s.ScriptId, s.ScriptName, s.Language.ToDbToken(), s.ScriptIsActive, s.ContinueOnFailure)).ToList());
}

public sealed record ChainStepSaveRequest(Guid ScriptId, bool ContinueOnFailure);

public sealed record ChainSaveRequest(
    string Name,
    string? Description,
    string? OnFailure,
    string? CronExpression,
    bool IsActive,
    string? NotifyOnFailureRecipient,
    Guid? NotifyOnFailureTemplateId,
    List<ChainStepSaveRequest> Steps);

public sealed record ChainActiveRequest(bool IsActive);

public sealed record ChainSummaryDto(
    Guid ChainId,
    string Name,
    string? Description,
    string OnFailure,
    string? CronExpression,
    DateTimeOffset? NextRunAt,
    bool IsActive,
    int StepCount,
    bool NotifiesOnFailure,
    DateTimeOffset CreatedAt,
    long? LastRunId,
    string? LastRunStatus,
    DateTimeOffset? LastRunStartedAt,
    int? LastRunDurationMs);

public sealed record ChainStepDto(
    int StepOrder,
    Guid ScriptId,
    string ScriptName,
    string Language,
    bool ScriptIsActive,
    bool ContinueOnFailure);

public sealed record ChainDetailDto(
    Guid ChainId,
    string Name,
    string? Description,
    string OnFailure,
    string? CronExpression,
    DateTimeOffset? NextRunAt,
    bool IsActive,
    string? NotifyOnFailureRecipient,
    Guid? NotifyOnFailureTemplateId,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ChainStepDto> Steps);
