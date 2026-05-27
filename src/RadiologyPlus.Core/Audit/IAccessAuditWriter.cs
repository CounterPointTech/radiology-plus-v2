using Microsoft.AspNetCore.Http;
using RadiologyPlus.Core.Identity;

namespace RadiologyPlus.Core.Audit;

/// <summary>
/// Writes application-level audit rows to audit.access_logs. Use for everything *except*
/// Novarad mutations (those go through INovaradWriter, which double-writes).
/// </summary>
public interface IAccessAuditWriter
{
    Task WriteSuccessAsync(
        Guid tenantId, AppUser user, AccessAction action, string description,
        HttpContext http, CancellationToken cancellationToken = default);

    Task WriteFailureAsync(
        Guid tenantId, string? username, AccessAction action, string reason,
        HttpContext http, CancellationToken cancellationToken = default);
}
