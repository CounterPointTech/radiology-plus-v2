namespace RadiologyPlus.Core.Identity;

// ---------------------------------------------------------------------------
// Admin-facing records for the Users console page. Two kinds of rows live in
// identity.users: LOCAL users (is_local, bcrypt hash, fully editable here) and
// FEDERATED Novarad users (profile overwritten from Novarad at every login —
// only is_active is admin-controlled for them).
// ---------------------------------------------------------------------------

public sealed record UserAdminSummary(
    Guid UserId,
    string Username,
    string DisplayName,
    string? Email,
    Role Role,
    bool IsLocal,
    bool IsActive,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset CreatedAt,
    IReadOnlyList<int> FacilityIds,
    int ActiveSessionCount);

/// <summary>Create-shape for a LOCAL user. PasswordHash is already bcrypt-hashed by the caller.</summary>
public sealed record LocalUserCreate(
    string Username,
    string DisplayName,
    string? Email,
    Role Role,
    string PasswordHash,
    IReadOnlyList<int> FacilityIds);

/// <summary>Update-shape for a LOCAL user (username is immutable).</summary>
public sealed record LocalUserUpdate(
    string DisplayName,
    string? Email,
    Role Role,
    IReadOnlyList<int> FacilityIds);

/// <summary>An unexpired, unrevoked refresh token — one active sign-in.</summary>
public sealed record UserSessionInfo(
    Guid TokenId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Management surface over identity.users / user_facilities / refresh_tokens.
/// Tenant-scoped: every query passes tenant_id explicitly (RLS enforces it too).
/// </summary>
public interface IUserAdminRepository
{
    Task<IReadOnlyList<UserAdminSummary>> ListAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task<UserAdminSummary?> GetAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default);

    Task<UserAdminSummary> CreateLocalAsync(Guid tenantId, LocalUserCreate input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Throws KeyNotFoundException when the user does not exist,
    /// InvalidOperationException when the user is federated (Novarad owns that profile).
    /// </summary>
    Task<UserAdminSummary> UpdateLocalAsync(Guid tenantId, Guid userId, LocalUserUpdate input, CancellationToken cancellationToken = default);

    /// <summary>Works for local AND federated users. Throws KeyNotFoundException when missing.</summary>
    Task<UserAdminSummary> SetActiveAsync(Guid tenantId, Guid userId, bool isActive, CancellationToken cancellationToken = default);

    /// <summary>
    /// Local users only (hash already bcrypt). Throws KeyNotFoundException / InvalidOperationException
    /// like <see cref="UpdateLocalAsync"/>.
    /// </summary>
    Task SetPasswordAsync(Guid tenantId, Guid userId, string passwordHash, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UserSessionInfo>> ListActiveSessionsAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Revokes every active refresh token (forces sign-out everywhere). Returns the count revoked.</summary>
    Task<int> RevokeAllSessionsAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default);
}
