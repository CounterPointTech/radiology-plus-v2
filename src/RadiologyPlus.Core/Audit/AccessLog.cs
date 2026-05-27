namespace RadiologyPlus.Core.Audit;

public enum AccessAction
{
    Login = 1,
    Logout = 2,
    Read = 3,
    Create = 4,
    Update = 5,
    Delete = 6,
    Execute = 7,
    NovaradWrite = 8,
    PermissionDenied = 9,
}

public sealed record AccessLogEntry(
    Guid TenantId,
    Guid? UserId,
    string? Username,
    AccessAction Action,
    string ResourceType,
    string? ResourceId,
    bool Success,
    string? IpAddress,
    string? UserAgent,
    string? ErrorMessage,
    object? Metadata,
    DateTimeOffset OccurredAt);
