using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Npgsql;
using RadiologyPlus.Core.Audit;
using RadiologyPlus.Core.Identity;
using RadiologyPlus.Data.Connections;
using RadiologyPlus.Scripting;

namespace RadiologyPlus.AdminApi.Endpoints;

/// <summary>
/// Script Manager surface (NRS only, enforced per-handler; every mutation audited).
/// Run-now is fire-and-forget through the real ScriptExecutionEngine — the UI polls
/// the executions list to watch it land.
/// </summary>
public static class ScriptsEndpoints
{
    public static IEndpointRouteBuilder MapScriptsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/scripts")
            .WithTags("Scripts")
            .RequireAuthorization();

        group.MapGet("/", ListScriptsAsync).WithName("ScriptsList");
        group.MapPost("/", CreateScriptAsync).WithName("ScriptsCreate");
        group.MapGet("/executions", ListAllExecutionsAsync).WithName("ScriptsExecutionsRecent");
        group.MapGet("/executions/{executionId:long}", GetExecutionAsync).WithName("ScriptsExecutionDetail");
        group.MapPost("/executions/{executionId:long}/cancel", CancelExecutionAsync).WithName("ScriptsExecutionCancel");
        group.MapGet("/versions/{versionId:guid}", GetVersionAsync).WithName("ScriptsVersionDetail");
        group.MapGet("/{scriptId:guid}", GetScriptAsync).WithName("ScriptsGet");
        group.MapPut("/{scriptId:guid}", UpdateScriptAsync).WithName("ScriptsUpdate");
        group.MapDelete("/{scriptId:guid}", DeleteScriptAsync).WithName("ScriptsDelete");
        group.MapPatch("/{scriptId:guid}/active", SetActiveAsync).WithName("ScriptsSetActive");
        group.MapPost("/{scriptId:guid}/run", RunScriptAsync).WithName("ScriptsRun");
        group.MapGet("/{scriptId:guid}/executions", ListScriptExecutionsAsync).WithName("ScriptsExecutionsForScript");
        group.MapGet("/{scriptId:guid}/versions", ListVersionsAsync).WithName("ScriptsVersions");
        group.MapPost("/test", RunSmokeTestAsync).WithName("ScriptsSmokeTest");

        return app;
    }

    // -- List / read ---------------------------------------------------------

    private static async Task<IResult> ListScriptsAsync(
        ICurrentUser currentUser, IScriptAdminRepository repo, CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessScripting()) return Results.Forbid();

        var now = DateTimeOffset.UtcNow;
        var rows = await repo.ListAllAsync(user.TenantId, ct);
        return Results.Ok(rows.Select(s => ToSummaryDto(s, now)).ToList());
    }

    private static async Task<IResult> GetScriptAsync(
        Guid scriptId, ICurrentUser currentUser, IScriptAdminRepository repo, CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessScripting()) return Results.Forbid();

        var script = await repo.GetDetailAsync(user.TenantId, scriptId, ct);
        return script is null
            ? Results.NotFound(new { error = "Script not found." })
            : Results.Ok(ToDetailDto(script));
    }

    // -- Create / update / delete / activate ----------------------------------

    private static async Task<IResult> CreateScriptAsync(
        ScriptSaveRequest req,
        ICurrentUser currentUser,
        IScriptAdminRepository repo,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessScripting()) return Results.Forbid();
        if (Validate(req, out var input) is { } bad) return bad;

        try
        {
            var created = await repo.CreateScriptAsync(user.TenantId, user.UserId, ToCreate(input), ct);
            await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Create,
                $"scripting.scripts script_id={created.ScriptId}: created '{created.Name}' ({created.Language.ToDbToken()}) active={created.IsActive}",
                http, ct);
            return Results.Ok(ToDetailDto(created));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Results.Conflict(new { error = $"A script named \"{req.Name}\" already exists." });
        }
    }

    private static async Task<IResult> UpdateScriptAsync(
        Guid scriptId,
        ScriptSaveRequest req,
        ICurrentUser currentUser,
        IScriptAdminRepository repo,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessScripting()) return Results.Forbid();
        if (Validate(req, out var input) is { } bad) return bad;

        try
        {
            var result = await repo.UpdateScriptAsync(user.TenantId, scriptId, user.UserId, ToUpdate(input), ct);
            var bodyChanged = !string.Equals(result.Before.Body, result.After.Body, StringComparison.Ordinal);
            await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Update,
                $"scripting.scripts script_id={scriptId}: updated '{result.After.Name}'" +
                (bodyChanged ? " (body changed, prior version snapshotted)" : ""),
                http, ct);
            return Results.Ok(ToDetailDto(result.After));
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { error = "Script not found." });
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Results.Conflict(new { error = $"A script named \"{req.Name}\" already exists." });
        }
    }

    private static async Task<IResult> SetActiveAsync(
        Guid scriptId,
        ScriptActiveRequest req,
        ICurrentUser currentUser,
        IScriptAdminRepository repo,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessScripting()) return Results.Forbid();

        try
        {
            var updated = await repo.SetScriptActiveAsync(user.TenantId, scriptId, req.IsActive, ct);
            await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Update,
                $"scripting.scripts script_id={scriptId}: active->{req.IsActive}", http, ct);
            return Results.Ok(ToDetailDto(updated));
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { error = "Script not found." });
        }
    }

    private static async Task<IResult> DeleteScriptAsync(
        Guid scriptId,
        ICurrentUser currentUser,
        IScriptAdminRepository repo,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessScripting()) return Results.Forbid();

        try
        {
            var removed = await repo.DeleteScriptAsync(user.TenantId, scriptId, ct);
            if (!removed) return Results.NotFound(new { error = "Script not found." });
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            return Results.Conflict(new { error = "This script is used by a script chain — remove it from the chain first." });
        }

        await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Delete,
            $"scripting.scripts script_id={scriptId} removed", http, ct);
        return Results.NoContent();
    }

    // -- Run / executions ------------------------------------------------------

    private static async Task<IResult> RunScriptAsync(
        Guid scriptId,
        ICurrentUser currentUser,
        IScriptAdminRepository repo,
        ScriptExecutionEngine engine,
        IAccessAuditWriter audit,
        ILoggerFactory loggerFactory,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessScripting()) return Results.Forbid();

        // Tenant-scoped existence check before handing off to the (unscoped) engine.
        var script = await repo.GetDetailAsync(user.TenantId, scriptId, ct);
        if (script is null) return Results.NotFound(new { error = "Script not found." });
        if (!script.IsActive) return Results.BadRequest(new { error = "Script is inactive — activate it before running." });

        await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Execute,
            $"scripting.scripts script_id={scriptId}: manual run of '{script.Name}' requested", http, ct);

        // Fire-and-forget: the engine creates + updates the execution row itself;
        // the UI polls the executions list to watch it. Deliberately NOT the request
        // token — the run must survive the HTTP response.
        var logger = loggerFactory.CreateLogger("RadiologyPlus.AdminApi.ScriptRun");
        var userId = user.UserId;
        _ = Task.Run(async () =>
        {
            try
            {
                await engine.RunAsync(scriptId, "manual", userId, cancellationToken: CancellationToken.None);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogError(ex, "Manual run of script {ScriptId} failed to start.", scriptId);
            }
        }, CancellationToken.None);

        return Results.Accepted(value: new { started = true, scriptId });
    }

    private static async Task<IResult> ListAllExecutionsAsync(
        [FromQuery] int? limit, ICurrentUser currentUser, IScriptAdminRepository repo, CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessScripting()) return Results.Forbid();
        var rows = await repo.ListExecutionsAsync(user.TenantId, null, Math.Clamp(limit ?? 50, 1, 200), ct);
        return Results.Ok(rows);
    }

    private static async Task<IResult> ListScriptExecutionsAsync(
        Guid scriptId, [FromQuery] int? limit, ICurrentUser currentUser, IScriptAdminRepository repo, CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessScripting()) return Results.Forbid();
        var rows = await repo.ListExecutionsAsync(user.TenantId, scriptId, Math.Clamp(limit ?? 50, 1, 200), ct);
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetExecutionAsync(
        long executionId, ICurrentUser currentUser, IScriptAdminRepository repo, CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessScripting()) return Results.Forbid();
        var row = await repo.GetExecutionAsync(user.TenantId, executionId, ct);
        return row is null ? Results.NotFound(new { error = "Execution not found." }) : Results.Ok(row);
    }

    private static async Task<IResult> CancelExecutionAsync(
        long executionId,
        ICurrentUser currentUser,
        IScriptAdminRepository repo,
        ScriptExecutionEngine engine,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessScripting()) return Results.Forbid();

        var row = await repo.GetExecutionAsync(user.TenantId, executionId, ct);
        if (row is null) return Results.NotFound(new { error = "Execution not found." });

        // Only executions running in THIS host's engine can be cancelled (manual runs).
        // Scheduled runs execute inside RadiologyPlus.AdminService.
        var cancelled = engine.TryCancel(executionId);
        if (cancelled)
        {
            await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Execute,
                $"scripting.executions execution_id={executionId}: cancel requested", http, ct);
        }

        return Results.Ok(new
        {
            cancelled,
            message = cancelled
                ? "Cancellation requested."
                : "This run isn't cancellable from here — it may have finished, or it's running in the scheduler service.",
        });
    }

    // -- Versions --------------------------------------------------------------

    private static async Task<IResult> ListVersionsAsync(
        Guid scriptId, ICurrentUser currentUser, IScriptAdminRepository repo, CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessScripting()) return Results.Forbid();
        var rows = await repo.ListVersionsAsync(user.TenantId, scriptId, ct);
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetVersionAsync(
        Guid versionId, ICurrentUser currentUser, IScriptAdminRepository repo, CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessScripting()) return Results.Forbid();
        var row = await repo.GetVersionAsync(user.TenantId, versionId, ct);
        return row is null ? Results.NotFound(new { error = "Version not found." }) : Results.Ok(row);
    }

    // -- Smoke test (kept from the scaffold) ------------------------------------

    /// <summary>
    /// Runs an inline <c>SELECT 1</c> against the app database through the real
    /// PostgreSQL script executor — proving DI, the executor factory, connection
    /// handling, and audit all work on this host.
    /// </summary>
    private static async Task<IResult> RunSmokeTestAsync(
        ICurrentUser currentUser,
        ScriptExecutorFactory executors,
        IOptions<AppDbOptions> dbOptions,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessScripting()) return Results.Forbid();

        var executor = executors.Get(ScriptLanguage.Pgsql);
        var invocation = new ScriptInvocation(
            Body: "SELECT 1 AS smoke_test",
            Language: ScriptLanguage.Pgsql,
            Parameters: null,
            ConnectionString: dbOptions.Value.ConnectionString,
            TimeoutSeconds: 15,
            MaxResultRows: 10);

        var result = await executor.ExecuteAsync(invocation, ct);

        await audit.WriteSuccessAsync(
            user.TenantId, user, AccessAction.Execute,
            $"scripts smoke test: {(result.Success ? "success" : "failed")} in {result.DurationMs}ms",
            http, ct);

        return Results.Ok(new
        {
            Ok = result.Success,
            Status = result.Status.ToString(),
            result.DurationMs,
            result.Output,
            result.Error,
        });
    }

    // -- Validation + mapping ----------------------------------------------------

    private sealed record ValidatedSave(
        string Name, string? Description, ScriptLanguage Language, string Body, string Target,
        string? Cron, bool IsActive, int TimeoutSeconds, Dictionary<string, object?>? Parameters);

    private static IResult? Validate(ScriptSaveRequest req, out ValidatedSave input)
    {
        input = null!;
        if (req is null) return Results.BadRequest(new { error = "Request body is required." });
        if (string.IsNullOrWhiteSpace(req.Name))
            return Results.BadRequest(new { error = "name is required." });
        if (string.IsNullOrWhiteSpace(req.Body))
            return Results.BadRequest(new { error = "body is required." });

        ScriptLanguage language;
        try
        {
            language = ScriptLanguageParser.Parse(req.Language ?? "");
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { error = "language must be one of: tsql, pgsql, powershell, batch." });
        }

        // Connection target must fit the language: SQL scripts need a database,
        // shell scripts run without one.
        var target = string.IsNullOrWhiteSpace(req.ConnectionTarget)
            ? language switch
            {
                ScriptLanguage.Pgsql => "appdb",
                ScriptLanguage.Tsql => "mmodal",
                _ => "none",
            }
            : req.ConnectionTarget.Trim().ToLowerInvariant();
        var targetOk = language switch
        {
            ScriptLanguage.Pgsql => target is "appdb" or "novarad",
            ScriptLanguage.Tsql => target is "mmodal",
            _ => target is "none",
        };
        if (!targetOk)
        {
            return Results.BadRequest(new
            {
                error = language switch
                {
                    ScriptLanguage.Pgsql => "pgsql scripts must target appdb or novarad.",
                    ScriptLanguage.Tsql => "tsql scripts must target mmodal.",
                    _ => "powershell/batch scripts don't use a connection (target must be none).",
                },
            });
        }

        var cron = string.IsNullOrWhiteSpace(req.CronExpression) ? null : req.CronExpression.Trim();
        if (cron is not null && !CronHelper.TryParse(cron, out _))
            return Results.BadRequest(new { error = $"\"{cron}\" is not a valid cron expression." });

        var timeout = req.TimeoutSeconds ?? 300;
        if (timeout is < 1 or > 86_400)
            return Results.BadRequest(new { error = "timeoutSeconds must be between 1 and 86400." });

        input = new ValidatedSave(
            req.Name.Trim(),
            string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim(),
            language, req.Body, target, cron, req.IsActive, timeout, req.Parameters);
        return null;
    }

    private static ScriptCreate ToCreate(ValidatedSave v) =>
        new(v.Name, v.Description, v.Language, v.Body, v.Target, v.Cron, v.IsActive, v.TimeoutSeconds, v.Parameters);

    private static ScriptUpdate ToUpdate(ValidatedSave v) =>
        new(v.Name, v.Description, v.Language, v.Body, v.Target, v.Cron, v.IsActive, v.TimeoutSeconds, v.Parameters);

    private static ScriptSummaryDto ToSummaryDto(ScriptSummary s, DateTimeOffset nowUtc) => new(
        s.ScriptId, s.Name, s.Description, s.Language.ToDbToken(), s.ConnectionTarget, s.CronExpression,
        CronHelper.NextOccurrenceUtc(s.IsActive ? s.CronExpression : null, nowUtc),
        s.IsActive, s.TimeoutSeconds, s.CreatedAt, s.UpdatedAt,
        s.LastExecutionId, s.LastStatus, s.LastStartedAt, s.LastDurationMs);

    private static ScriptDetailDto ToDetailDto(ScriptDetail s) => new(
        s.ScriptId, s.Name, s.Description, s.Language.ToDbToken(), s.Body, s.ConnectionTarget, s.CronExpression,
        CronHelper.NextOccurrenceUtc(s.IsActive ? s.CronExpression : null, DateTimeOffset.UtcNow),
        s.IsActive, s.TimeoutSeconds, s.Parameters, s.CreatedBy, s.CreatedAt, s.UpdatedAt);
}

public sealed record ScriptSaveRequest(
    string Name,
    string? Description,
    string? Language,
    string Body,
    string? ConnectionTarget,
    string? CronExpression,
    bool IsActive,
    int? TimeoutSeconds,
    Dictionary<string, object?>? Parameters);

public sealed record ScriptActiveRequest(bool IsActive);

public sealed record ScriptSummaryDto(
    Guid ScriptId,
    string Name,
    string? Description,
    string Language,
    string ConnectionTarget,
    string? CronExpression,
    DateTimeOffset? NextRunAt,
    bool IsActive,
    int TimeoutSeconds,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long? LastExecutionId,
    string? LastStatus,
    DateTimeOffset? LastStartedAt,
    int? LastDurationMs);

public sealed record ScriptDetailDto(
    Guid ScriptId,
    string Name,
    string? Description,
    string Language,
    string Body,
    string ConnectionTarget,
    string? CronExpression,
    DateTimeOffset? NextRunAt,
    bool IsActive,
    int TimeoutSeconds,
    IReadOnlyDictionary<string, object?>? Parameters,
    Guid? CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
