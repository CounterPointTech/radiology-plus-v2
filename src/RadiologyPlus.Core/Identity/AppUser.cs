namespace RadiologyPlus.Core.Identity;

public sealed record AppUser(
    Guid UserId,
    Guid TenantId,
    string Username,
    string DisplayName,
    Role Role,
    bool IsLocal,
    IReadOnlyList<int> FacilityIds,
    DateTimeOffset? LastLoginAt);
