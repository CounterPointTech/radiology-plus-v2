using Microsoft.AspNetCore.Mvc;
using RadiologyPlus.Core.Audit;
using RadiologyPlus.Core.Identity;

namespace RadiologyPlus.AdminApi.Endpoints;

/// <summary>
/// Read-only audit log viewer (NRS/Admin). The write side has been collecting
/// since day one — this is the first surface that shows it.
/// </summary>
public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/audit")
            .WithTags("Audit")
            .RequireAuthorization();

        group.MapGet("/", ListAsync).WithName("AuditList");

        return app;
    }

    private static async Task<IResult> ListAsync(
        [FromQuery] string? username,
        [FromQuery] string? action,
        [FromQuery] bool? success,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        ICurrentUser currentUser,
        IAccessAuditReader reader,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();

        AccessAction? actionFilter = null;
        if (!string.IsNullOrWhiteSpace(action))
        {
            if (!Enum.TryParse<AccessAction>(action, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                return Results.BadRequest(new { error = "Unknown action filter." });
            actionFilter = parsed;
        }

        var page = await reader.ListAsync(user.TenantId, new AccessLogFilter(
            Username: string.IsNullOrWhiteSpace(username) ? null : username.Trim(),
            Action: actionFilter,
            Success: success,
            From: from,
            To: to,
            Limit: Math.Clamp(limit ?? 50, 1, 200),
            Offset: Math.Max(offset ?? 0, 0)), ct);

        return Results.Ok(new
        {
            items = page.Items.Select(r => new
            {
                r.LogId,
                r.UserId,
                r.Username,
                Action = r.Action.ToString(),
                r.ResourceType,
                r.ResourceId,
                r.Success,
                r.IpAddress,
                r.UserAgent,
                r.ErrorMessage,
                r.MetadataJson,
                r.OccurredAt,
            }).ToList(),
            page.Total,
        });
    }
}
