namespace RadiologyPlus.Core.Identity;

/// <summary>
/// Validates a Novarad user's credentials against a tenant's NovaRIS/NovaPACS instance.
/// Strategy interface so we can swap the implementation as we learn more about Novarad's
/// password storage / Remoting auth surface.
/// </summary>
public interface INovaradCredentialValidator
{
    Task<NovaradCredentialResult> ValidateAsync(
        Guid tenantId,
        string username,
        string password,
        CancellationToken cancellationToken = default);
}

public sealed record NovaradCredentialResult(
    bool IsValid,
    string? DisplayName,
    string? Email,
    Role MappedRole,
    IReadOnlyList<int> FacilityIds,
    string? FailureReason);
