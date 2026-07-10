using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using RadiologyPlus.Core.Identity;
using RadiologyPlus.Core.Tenancy;

namespace RadiologyPlus.WebShared.Auth;

/// <summary>
/// After ASP.NET Core authentication populates HttpContext.User from the JWT, this
/// middleware copies tenant + user into the ambient async-local accessors so the rest
/// of the stack (data layer, audit, scripting) can resolve them without depending on HttpContext.
/// </summary>
public sealed class TenantAndUserMiddleware
{
    private readonly RequestDelegate _next;

    public TenantAndUserMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext context,
        ITenantContextAccessor tenantAccessor,
        ICurrentUser currentUser,
        ITenantRepository tenants,
        ILogger<TenantAndUserMiddleware> logger)
    {
        var principal = context.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            await _next(context);
            return;
        }

        if (!Guid.TryParse(principal.FindFirst("tid")?.Value, out var tenantId) ||
            !Guid.TryParse(principal.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value, out var userId))
        {
            logger.LogWarning("Authenticated request missing tenant/user claims.");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var tenant = await tenants.GetByIdAsync(tenantId, context.RequestAborted);
        if (tenant is null || !tenant.IsActive)
        {
            logger.LogWarning("Token references inactive or missing tenant {TenantId}.", tenantId);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var roleString = principal.FindFirst(ClaimTypes.Role)?.Value;
        if (!Enum.TryParse<Role>(roleString, out var role))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var facilityIds = principal.FindAll("fid")
            .Select(c => int.TryParse(c.Value, out var v) ? v : -1)
            .Where(v => v > 0)
            .ToArray();

        var user = new AppUser(
            UserId: userId,
            TenantId: tenantId,
            Username: principal.FindFirst("name")?.Value ?? "",
            DisplayName: principal.FindFirst("dn")?.Value ?? "",
            Role: role,
            IsLocal: role == Role.NRS,
            FacilityIds: facilityIds,
            LastLoginAt: null);

        using var _tenantScope = tenantAccessor.Push(new TenantContext(tenant.TenantId, tenant.Code, tenant.DisplayName));
        using var _userScope = currentUser.Push(user);

        await _next(context);
    }
}

public static class AuthMiddlewareExtensions
{
    public static IApplicationBuilder UseTenantAndUserContext(this IApplicationBuilder app)
        => app.UseMiddleware<TenantAndUserMiddleware>();
}
