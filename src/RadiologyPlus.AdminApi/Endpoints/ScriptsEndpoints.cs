using Microsoft.Extensions.Options;
using RadiologyPlus.Core.Audit;
using RadiologyPlus.Core.Identity;
using RadiologyPlus.Data.Connections;
using RadiologyPlus.Scripting;

namespace RadiologyPlus.AdminApi.Endpoints;

/// <summary>
/// Script Manager surface (NRS only). This round is a scaffold: the real CRUD +
/// scheduling UI is deferred, but <c>POST /scripts/test</c> genuinely exercises the
/// scripting engine's executor path end-to-end so the split can be smoke-tested.
/// </summary>
public static class ScriptsEndpoints
{
    public static IEndpointRouteBuilder MapScriptsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/scripts")
            .WithTags("Scripts")
            .RequireAuthorization();

        group.MapGet("/status", GetStatusAsync).WithName("ScriptsStatus");
        group.MapPost("/test", RunSmokeTestAsync).WithName("ScriptsSmokeTest");

        return app;
    }

    private static IResult GetStatusAsync(ICurrentUser currentUser, ScriptExecutorFactory executors)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessScripting()) return Results.Forbid();

        return Results.Ok(new
        {
            Scaffold = true,
            Message = "Script Manager is coming soon. The execution engine is live — use POST /scripts/test to verify it.",
            SupportedLanguages = executors.SupportedLanguages.Select(l => l.ToDbToken()).Order().ToArray(),
        });
    }

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
}
