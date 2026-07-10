namespace RadiologyPlus.Core.Audit;

/// <summary>One page of the audit log plus the unpaged filtered total.</summary>
public sealed record AccessLogPage(
    IReadOnlyList<AccessLogRow> Items,
    int Total);

/// <summary>A stored audit.access_logs row (metadata as raw JSON text).</summary>
public sealed record AccessLogRow(
    long LogId,
    Guid? UserId,
    string? Username,
    AccessAction Action,
    string ResourceType,
    string? ResourceId,
    bool Success,
    string? IpAddress,
    string? UserAgent,
    string? ErrorMessage,
    string? MetadataJson,
    DateTimeOffset OccurredAt);

public sealed record AccessLogFilter(
    string? Username,
    AccessAction? Action,
    bool? Success,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int Limit,
    int Offset);

/// <summary>
/// Read surface over audit.access_logs for the console's audit viewer.
/// Tenant-scoped: every query passes tenant_id explicitly (RLS enforces it too).
/// </summary>
public interface IAccessAuditReader
{
    Task<AccessLogPage> ListAsync(Guid tenantId, AccessLogFilter filter, CancellationToken cancellationToken = default);
}
