using RadiologyPlus.Core.Identity;

namespace RadiologyPlus.AdminApi.Endpoints;

/// <summary>
/// Notifications surface (NRS/Admin). Scaffold this round — the orchestrator runs in
/// RadiologyPlus.AdminService; the management UI and CRUD endpoints come later.
/// </summary>
public static class NotificationsEndpoints
{
    public static IEndpointRouteBuilder MapNotificationsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/notifications")
            .WithTags("Notifications")
            .RequireAuthorization();

        group.MapGet("/status", GetStatus).WithName("NotificationsStatus");

        return app;
    }

    private static IResult GetStatus(ICurrentUser currentUser)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();

        return Results.Ok(new
        {
            Scaffold = true,
            Message = "Notifications management is coming soon. The orchestrator runs in RadiologyPlus.AdminService.",
        });
    }
}
