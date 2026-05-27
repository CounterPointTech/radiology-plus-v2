using System.Data;

namespace RadiologyPlus.Core.Audit;

/// <summary>
/// Mandatory wrapper for every write into a tenant's NovaPACS / NovaRIS database.
/// Produces dual-audit rows (Novarad-side + our own audit.access_logs) inside a single transaction
/// so that they roll back together on failure.
/// </summary>
/// <remarks>
/// Business code MUST NOT call <c>INovaradDbContext.ExecuteAsync</c> for mutations.
/// Reads are unrestricted; mutations must go through this interface.
/// </remarks>
public interface INovaradWriter
{
    /// <summary>
    /// Execute one or more Novarad SQL mutations inside a transaction, with dual-audit recording.
    /// </summary>
    /// <param name="action">Performs the actual SQL writes against the transaction's connection.</param>
    /// <param name="description">Required. Short verb-phrase describing the write — surfaced into the audit row.</param>
    /// <param name="reason">Required. Business reason (e.g., the tech wizard's "Reason for Exam" or the user's free-text note).</param>
    /// <param name="resourceType">e.g. "pacs.studies", "ris.orders". Goes into our audit.access_logs.</param>
    /// <param name="resourceId">Identifier of the row being mutated. Goes into our audit.access_logs.</param>
    /// <param name="beforeSnapshot">Optional JSON snapshot of the row(s) before the write.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The audit record produced for this operation.</returns>
    Task<NovaradAuditRecord> ExecuteAsync(
        Func<IDbConnection, IDbTransaction, CancellationToken, Task<object?>> action,
        string description,
        string reason,
        string resourceType,
        string resourceId,
        object? beforeSnapshot = null,
        CancellationToken cancellationToken = default);
}

public sealed record NovaradAuditRecord(
    long AuditId,
    Guid TenantId,
    Guid UserId,
    string Username,
    string Action,
    string ResourceType,
    string ResourceId,
    string Reason,
    DateTimeOffset OccurredAt,
    bool Success,
    string? ErrorMessage);
