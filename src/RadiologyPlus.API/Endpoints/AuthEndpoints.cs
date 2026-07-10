using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using RadiologyPlus.WebShared.Auth;
using RadiologyPlus.Common.Security;
using RadiologyPlus.Core.Audit;
using RadiologyPlus.Core.Identity;
using RadiologyPlus.Data.Tenancy;

namespace RadiologyPlus.API.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").WithTags("Auth");

        group.MapPost("/login", LoginAsync).WithName("Login");
        group.MapPost("/refresh", RefreshAsync).WithName("RefreshToken");
        group.MapPost("/logout", LogoutAsync).RequireAuthorization().WithName("Logout");
        group.MapGet("/me", MeAsync).RequireAuthorization().WithName("Me");

        return app;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest req,
        TenantRepository tenants,
        IIdentityRepository identity,
        INovaradCredentialValidator novaradValidator,
        IPasswordHasher hasher,
        IJwtTokenService jwt,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Facility) ||
            string.IsNullOrWhiteSpace(req.Username) ||
            string.IsNullOrWhiteSpace(req.Password))
        {
            return Results.BadRequest(new { error = "Facility, username, and password are required." });
        }

        var tenant = await tenants.GetByFacilityCodeAsync(req.Facility, ct);
        if (tenant is null)
        {
            await audit.WriteFailureAsync(Guid.Empty, req.Username, AccessAction.Login, "Unknown facility", http, ct);
            return Results.Unauthorized();
        }

        // 1) Local user path (NRS or seeded local accounts)
        var local = await identity.GetByUsernameAsync(tenant.TenantId, req.Username, ct);
        if (local is { IsActive: true, IsLocal: true, PasswordHash: not null })
        {
            if (!hasher.Verify(req.Password, local.PasswordHash))
            {
                await audit.WriteFailureAsync(tenant.TenantId, req.Username, AccessAction.Login, "Bad password (local)", http, ct);
                return Results.Unauthorized();
            }

            return await IssueTokensAsync(local, tenant.TenantId, identity, jwt, audit, http, ct);
        }

        // 2) Federated Novarad path
        var federated = await novaradValidator.ValidateAsync(tenant.TenantId, req.Username, req.Password, ct);
        if (!federated.IsValid)
        {
            await audit.WriteFailureAsync(tenant.TenantId, req.Username, AccessAction.Login, federated.FailureReason ?? "Invalid Novarad credentials", http, ct);
            return Results.Unauthorized();
        }

        var userId = await identity.UpsertFederatedAsync(new FederatedUserUpsert(
            tenant.TenantId, req.Username, federated.DisplayName ?? req.Username,
            federated.Email, federated.MappedRole, federated.FacilityIds), ct);

        var cached = await identity.GetByIdAsync(userId, ct);
        if (cached is null)
        {
            return Results.Problem("Failed to load federated user after upsert.");
        }
        // Valid Novarad credentials are not enough — an admin-deactivated account stays blocked.
        if (!cached.IsActive)
        {
            await audit.WriteFailureAsync(tenant.TenantId, req.Username, AccessAction.Login, "Account is deactivated", http, ct);
            return Results.Unauthorized();
        }
        return await IssueTokensAsync(cached, tenant.TenantId, identity, jwt, audit, http, ct);
    }

    private static async Task<IResult> IssueTokensAsync(
        LocalUserRecord user, Guid tenantId, IIdentityRepository identity, IJwtTokenService jwt,
        IAccessAuditWriter audit, HttpContext http, CancellationToken ct)
    {
        var facilityIds = await identity.GetFacilityIdsAsync(user.UserId, ct);
        var appUser = new AppUser(
            user.UserId, tenantId, user.Username, user.DisplayName,
            user.Role, user.IsLocal, facilityIds, DateTimeOffset.UtcNow);

        var access = jwt.IssueAccessToken(appUser);
        var refreshRaw = jwt.IssueRefreshTokenRaw();
        var refreshHash = jwt.HashRefreshToken(refreshRaw);
        await identity.StoreRefreshTokenAsync(tenantId, user.UserId, refreshHash, DateTimeOffset.UtcNow.AddDays(14), ct);
        await identity.UpdateLastLoginAsync(user.UserId, DateTimeOffset.UtcNow, ct);
        await audit.WriteSuccessAsync(tenantId, appUser, AccessAction.Login, "Logged in", http, ct);

        return Results.Ok(new LoginResponse(
            AccessToken: access.Token,
            RefreshToken: refreshRaw,
            ExpiresInSeconds: access.ExpiresInSeconds,
            User: new UserDto(user.UserId, user.Username, user.DisplayName, user.Email, user.Role.ToString(), facilityIds.ToArray())));
    }

    private static async Task<IResult> RefreshAsync(
        RefreshRequest req,
        IIdentityRepository identity,
        IJwtTokenService jwt,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.RefreshToken))
            return Results.BadRequest(new { error = "refreshToken is required." });

        var hash = jwt.HashRefreshToken(req.RefreshToken);
        var found = await identity.ValidateRefreshTokenAsync(hash, ct);
        if (found is null) return Results.Unauthorized();

        var user = await identity.GetByIdAsync(found.Value.UserId, ct);
        if (user is null || !user.IsActive) return Results.Unauthorized();

        // Rotate: revoke old, issue new
        await identity.RevokeRefreshTokenAsync(found.Value.TokenId, ct);

        var facilityIds = await identity.GetFacilityIdsAsync(user.UserId, ct);
        var appUser = new AppUser(
            user.UserId, found.Value.TenantId, user.Username, user.DisplayName,
            user.Role, user.IsLocal, facilityIds, DateTimeOffset.UtcNow);
        var access = jwt.IssueAccessToken(appUser);
        var refreshRaw = jwt.IssueRefreshTokenRaw();
        await identity.StoreRefreshTokenAsync(
            found.Value.TenantId, user.UserId, jwt.HashRefreshToken(refreshRaw),
            DateTimeOffset.UtcNow.AddDays(14), ct);

        return Results.Ok(new LoginResponse(
            AccessToken: access.Token,
            RefreshToken: refreshRaw,
            ExpiresInSeconds: access.ExpiresInSeconds,
            User: new UserDto(user.UserId, user.Username, user.DisplayName, user.Email, user.Role.ToString(), facilityIds.ToArray())));
    }

    [Authorize]
    private static async Task<IResult> LogoutAsync(
        ICurrentUser currentUser,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Current;
        if (user is null) return Results.NoContent();
        await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Logout, "Logged out", http, ct);
        return Results.NoContent();
    }

    [Authorize]
    private static IResult MeAsync(ICurrentUser currentUser)
    {
        var user = currentUser.Current;
        if (user is null) return Results.Unauthorized();
        return Results.Ok(new UserDto(
            user.UserId, user.Username, user.DisplayName, null,
            user.Role.ToString(), user.FacilityIds.ToArray()));
    }
}

public sealed record LoginRequest(string Facility, string Username, string Password);
public sealed record LoginResponse(string AccessToken, string RefreshToken, int ExpiresInSeconds, UserDto User);
public sealed record RefreshRequest(string RefreshToken);
public sealed record UserDto(Guid UserId, string Username, string DisplayName, string? Email, string Role, int[] FacilityIds);
