using Npgsql;
using RadiologyPlus.Common.Security;
using RadiologyPlus.Core.Audit;
using RadiologyPlus.Core.Identity;

namespace RadiologyPlus.AdminApi.Endpoints;

/// <summary>
/// Users console surface (NRS/Admin, enforced per-handler; every mutation audited).
/// Local users are fully manageable here; federated Novarad users only allow
/// activate/deactivate (their profile is Novarad's). Guardrails: NRS accounts can
/// only be managed by NRS, and nobody can deactivate themselves or change their
/// own role.
/// </summary>
public static class UsersEndpoints
{
    private const int MinPasswordLength = 12;

    public static IEndpointRouteBuilder MapUsersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/users")
            .WithTags("Users")
            .RequireAuthorization();

        group.MapGet("/", ListAsync).WithName("UsersList");
        group.MapPost("/", CreateAsync).WithName("UsersCreate");
        group.MapPut("/{userId:guid}", UpdateAsync).WithName("UsersUpdate");
        group.MapPatch("/{userId:guid}/active", SetActiveAsync).WithName("UsersSetActive");
        group.MapPost("/{userId:guid}/password", SetPasswordAsync).WithName("UsersSetPassword");
        group.MapGet("/{userId:guid}/sessions", ListSessionsAsync).WithName("UsersSessions");
        group.MapPost("/{userId:guid}/sessions/revoke", RevokeSessionsAsync).WithName("UsersRevokeSessions");

        return app;
    }

    private static async Task<IResult> ListAsync(
        ICurrentUser currentUser, IUserAdminRepository repo, CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();
        var rows = await repo.ListAsync(user.TenantId, ct);
        return Results.Ok(rows.Select(ToDto).ToList());
    }

    private static async Task<IResult> CreateAsync(
        UserCreateRequest req,
        ICurrentUser currentUser,
        IUserAdminRepository repo,
        IPasswordHasher hasher,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();

        if (req is null) return Results.BadRequest(new { error = "Request body is required." });
        if (string.IsNullOrWhiteSpace(req.Username))
            return Results.BadRequest(new { error = "username is required." });
        if (string.IsNullOrWhiteSpace(req.DisplayName))
            return Results.BadRequest(new { error = "displayName is required." });
        if (!TryParseRole(req.Role, out var role))
            return Results.BadRequest(new { error = "role must be one of: NRS, Admin, Tech, Radiologist." });
        if (string.IsNullOrEmpty(req.Password) || req.Password.Length < MinPasswordLength)
            return Results.BadRequest(new { error = $"password must be at least {MinPasswordLength} characters." });
        // Only NRS may mint NRS accounts.
        if (role == Role.NRS && user.Role != Role.NRS) return Results.Forbid();

        try
        {
            var created = await repo.CreateLocalAsync(user.TenantId, new LocalUserCreate(
                req.Username.Trim(),
                req.DisplayName.Trim(),
                NormalizeEmail(req.Email),
                role,
                hasher.Hash(req.Password),
                req.FacilityIds ?? []), ct);

            await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Create,
                $"identity.users user_id={created.UserId}: created local user '{created.Username}' ({created.Role})", http, ct);
            return Results.Ok(ToDto(created));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Results.Conflict(new { error = $"A user named \"{req.Username.Trim()}\" already exists." });
        }
    }

    private static async Task<IResult> UpdateAsync(
        Guid userId,
        UserUpdateRequest req,
        ICurrentUser currentUser,
        IUserAdminRepository repo,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();

        if (req is null) return Results.BadRequest(new { error = "Request body is required." });
        if (string.IsNullOrWhiteSpace(req.DisplayName))
            return Results.BadRequest(new { error = "displayName is required." });
        if (!TryParseRole(req.Role, out var role))
            return Results.BadRequest(new { error = "role must be one of: NRS, Admin, Tech, Radiologist." });

        var target = await repo.GetAsync(user.TenantId, userId, ct);
        if (target is null) return Results.NotFound(new { error = "User not found." });
        if (!MayManage(user, target)) return Results.Forbid();
        if (role == Role.NRS && user.Role != Role.NRS) return Results.Forbid();
        if (userId == user.UserId && role != target.Role)
            return Results.BadRequest(new { error = "You can't change your own role." });

        try
        {
            var updated = await repo.UpdateLocalAsync(user.TenantId, userId, new LocalUserUpdate(
                req.DisplayName.Trim(), NormalizeEmail(req.Email), role, req.FacilityIds ?? []), ct);

            await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Update,
                $"identity.users user_id={userId}: updated '{updated.Username}' (role={updated.Role})", http, ct);
            return Results.Ok(ToDto(updated));
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { error = "User not found." });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> SetActiveAsync(
        Guid userId,
        UserActiveRequest req,
        ICurrentUser currentUser,
        IUserAdminRepository repo,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();
        if (userId == user.UserId && !req.IsActive)
            return Results.BadRequest(new { error = "You can't deactivate your own account." });

        var target = await repo.GetAsync(user.TenantId, userId, ct);
        if (target is null) return Results.NotFound(new { error = "User not found." });
        if (!MayManage(user, target)) return Results.Forbid();

        // Blocking someone should bite immediately, not when their access token expires.
        // Revoke first so the returned row reports the post-revocation session count.
        var revoked = req.IsActive ? 0 : await repo.RevokeAllSessionsAsync(user.TenantId, userId, ct);
        var updated = await repo.SetActiveAsync(user.TenantId, userId, req.IsActive, ct);

        await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Update,
            $"identity.users user_id={userId}: active->{req.IsActive}" +
            (revoked > 0 ? $" ({revoked} session(s) revoked)" : ""), http, ct);
        return Results.Ok(ToDto(updated));
    }

    private static async Task<IResult> SetPasswordAsync(
        Guid userId,
        UserPasswordRequest req,
        ICurrentUser currentUser,
        IUserAdminRepository repo,
        IPasswordHasher hasher,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();
        if (req is null || string.IsNullOrEmpty(req.Password) || req.Password.Length < MinPasswordLength)
            return Results.BadRequest(new { error = $"password must be at least {MinPasswordLength} characters." });

        var target = await repo.GetAsync(user.TenantId, userId, ct);
        if (target is null) return Results.NotFound(new { error = "User not found." });
        if (!MayManage(user, target)) return Results.Forbid();

        try
        {
            await repo.SetPasswordAsync(user.TenantId, userId, hasher.Hash(req.Password), ct);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        // A reset password means existing sign-ins are stale — cut them off.
        var revoked = await repo.RevokeAllSessionsAsync(user.TenantId, userId, ct);
        await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Update,
            $"identity.users user_id={userId}: password reset ({revoked} session(s) revoked)", http, ct);
        return Results.Ok(new { ok = true, sessionsRevoked = revoked });
    }

    private static async Task<IResult> ListSessionsAsync(
        Guid userId, ICurrentUser currentUser, IUserAdminRepository repo, CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();
        var target = await repo.GetAsync(user.TenantId, userId, ct);
        if (target is null) return Results.NotFound(new { error = "User not found." });
        return Results.Ok(await repo.ListActiveSessionsAsync(user.TenantId, userId, ct));
    }

    private static async Task<IResult> RevokeSessionsAsync(
        Guid userId,
        ICurrentUser currentUser,
        IUserAdminRepository repo,
        IAccessAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = currentUser.Require();
        if (!user.Role.CanAccessAdmin()) return Results.Forbid();

        var target = await repo.GetAsync(user.TenantId, userId, ct);
        if (target is null) return Results.NotFound(new { error = "User not found." });
        if (!MayManage(user, target)) return Results.Forbid();

        var revoked = await repo.RevokeAllSessionsAsync(user.TenantId, userId, ct);
        await audit.WriteSuccessAsync(user.TenantId, user, AccessAction.Update,
            $"identity.refresh_tokens user_id={userId}: {revoked} session(s) revoked", http, ct);
        return Results.Ok(new { revoked });
    }

    // -- Helpers ---------------------------------------------------------------

    /// <summary>Admins manage everyone except NRS accounts; NRS manages everyone.</summary>
    private static bool MayManage(AppUser caller, UserAdminSummary target) =>
        caller.Role == Role.NRS || target.Role != Role.NRS;

    private static bool TryParseRole(string? token, out Role role) =>
        Enum.TryParse(token, ignoreCase: true, out role) && Enum.IsDefined(role);

    private static string? NormalizeEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim();

    private static AdminUserDto ToDto(UserAdminSummary u) => new(
        u.UserId, u.Username, u.DisplayName, u.Email, u.Role.ToString(), u.IsLocal,
        u.IsActive, u.LastLoginAt, u.CreatedAt, u.FacilityIds, u.ActiveSessionCount);
}

public sealed record UserCreateRequest(
    string Username,
    string DisplayName,
    string? Email,
    string Role,
    string Password,
    List<int>? FacilityIds);

public sealed record UserUpdateRequest(
    string DisplayName,
    string? Email,
    string Role,
    List<int>? FacilityIds);

public sealed record UserActiveRequest(bool IsActive);

public sealed record UserPasswordRequest(string Password);

public sealed record AdminUserDto(
    Guid UserId,
    string Username,
    string DisplayName,
    string? Email,
    string Role,
    bool IsLocal,
    bool IsActive,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset CreatedAt,
    IReadOnlyList<int> FacilityIds,
    int ActiveSessionCount);
