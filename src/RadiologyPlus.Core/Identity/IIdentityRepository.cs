namespace RadiologyPlus.Core.Identity;

public interface IIdentityRepository
{
    /// <summary>Local user lookup (NRS or federated-cache row).</summary>
    Task<LocalUserRecord?> GetByUsernameAsync(Guid tenantId, string username, CancellationToken cancellationToken = default);

    Task<LocalUserRecord?> GetByIdAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<Guid> UpsertFederatedAsync(FederatedUserUpsert upsert, CancellationToken cancellationToken = default);

    Task UpdateLastLoginAsync(Guid userId, DateTimeOffset at, CancellationToken cancellationToken = default);

    Task<Guid> StoreRefreshTokenAsync(
        Guid tenantId, Guid userId, string tokenHash, DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default);

    Task<(Guid TokenId, Guid UserId, Guid TenantId)?> ValidateRefreshTokenAsync(
        string tokenHash, CancellationToken cancellationToken = default);

    Task RevokeRefreshTokenAsync(Guid tokenId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<int>> GetFacilityIdsAsync(Guid userId, CancellationToken cancellationToken = default);
}

public sealed record LocalUserRecord(
    Guid UserId,
    Guid TenantId,
    string Username,
    string DisplayName,
    string? Email,
    Role Role,
    bool IsLocal,
    string? PasswordHash,
    bool IsActive);

public sealed record FederatedUserUpsert(
    Guid TenantId,
    string Username,
    string DisplayName,
    string? Email,
    Role Role,
    IReadOnlyList<int> FacilityIds);
