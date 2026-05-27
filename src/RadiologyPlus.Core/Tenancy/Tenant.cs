namespace RadiologyPlus.Core.Tenancy;

public sealed record Tenant(
    Guid TenantId,
    string Code,
    string DisplayName,
    bool IsActive,
    DateTimeOffset CreatedAt);

public sealed record TenantNovaradConnection(
    Guid TenantId,
    string Host,
    int Port,
    string Database,
    string Username,
    string EncryptedPassword,
    bool UseSsl);

public interface ITenantRepository
{
    Task<Tenant?> GetByCodeAsync(string code, CancellationToken cancellationToken = default);
    Task<Tenant?> GetByIdAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Tenant>> ListActiveAsync(CancellationToken cancellationToken = default);
}
