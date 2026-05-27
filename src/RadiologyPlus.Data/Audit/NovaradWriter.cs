using System.Data;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using RadiologyPlus.Core.Audit;
using RadiologyPlus.Core.Data;
using RadiologyPlus.Core.Identity;
using RadiologyPlus.Core.Tenancy;

namespace RadiologyPlus.Data.Audit;

/// <summary>
/// Concrete dual-audit writer. For each Novarad mutation:
///   1. Opens a Novarad connection + transaction
///   2. Runs the caller's SQL
///   3. INSERTs a row into the tenant's configured Novarad audit table
///   4. INSERTs a row into our own audit.access_logs
///   5. Commits (or rolls back all three together on failure)
/// </summary>
public sealed class NovaradWriter : INovaradWriter
{
    private readonly INovaradDbContext _novarad;
    private readonly IAppDbContext _appDb;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<NovaradWriter> _logger;

    public NovaradWriter(
        INovaradDbContext novarad,
        IAppDbContext appDb,
        ITenantContextAccessor tenantAccessor,
        ICurrentUser currentUser,
        ILogger<NovaradWriter> logger)
    {
        _novarad = novarad;
        _appDb = appDb;
        _tenantAccessor = tenantAccessor;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<NovaradAuditRecord> ExecuteAsync(
        Func<IDbConnection, IDbTransaction, CancellationToken, Task<object?>> action,
        string description,
        string reason,
        string resourceType,
        string resourceId,
        object? beforeSnapshot = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);

        var tenant = _tenantAccessor.Require();
        var user = _currentUser.Require();
        var occurredAt = DateTimeOffset.UtcNow;
        var novaradAuditTable = await GetNovaradAuditTableAsync(tenant.TenantId, cancellationToken);

        var conn = (NpgsqlConnection)await _novarad.OpenAsync(cancellationToken);
        await using var _ = conn;
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        object? afterSnapshot = null;
        Exception? failure = null;
        try
        {
            afterSnapshot = await action(conn, tx, cancellationToken);

            await InsertNovaradAuditAsync(conn, tx, novaradAuditTable,
                actor: $"RadiologyPlus:{user.Username}",
                operation: description,
                details: reason,
                resourceType: resourceType,
                resourceId: resourceId,
                occurredAt: occurredAt,
                cancellationToken);
        }
        catch (Exception ex)
        {
            failure = ex;
            _logger.LogError(ex, "Novarad write '{Description}' failed for tenant {Tenant} resource {Resource}/{Id}.",
                description, tenant.TenantId, resourceType, resourceId);
            await tx.RollbackAsync(cancellationToken);
        }

        long appAuditId = await InsertAppAccessLogAsync(
            tenant.TenantId, user, description, resourceType, resourceId,
            failure is null, reason, beforeSnapshot, afterSnapshot, failure?.Message,
            occurredAt, cancellationToken);

        if (failure is not null)
        {
            throw new InvalidOperationException(
                $"Novarad write '{description}' failed: {failure.Message}", failure);
        }

        await tx.CommitAsync(cancellationToken);

        return new NovaradAuditRecord(
            appAuditId,
            tenant.TenantId,
            user.UserId,
            user.Username,
            description,
            resourceType,
            resourceId,
            reason,
            occurredAt,
            Success: true,
            ErrorMessage: null);
    }

    private async Task<string> GetNovaradAuditTableAsync(Guid tenantId, CancellationToken ct)
    {
        await using var appConn = (NpgsqlConnection)await _appDb.OpenUnscopedAsync(ct);
        await using var cmd = appConn.CreateCommand();
        cmd.CommandText = "SELECT novarad_audit_table FROM tenancy.novarad_connections WHERE tenant_id = @t";
        cmd.Parameters.AddWithValue("t", tenantId);
        var value = await cmd.ExecuteScalarAsync(ct);
        // Base name of Novarad's year-partitioned audit table; the writer appends
        // "_<year>" to land in the right partition (e.g. shared.audit_2026).
        return value as string ?? "shared.audit";
    }

    private static async Task InsertNovaradAuditAsync(
        NpgsqlConnection conn, IDbTransaction tx, string auditTableBase,
        string actor, string operation, string details, string resourceType, string resourceId,
        DateTimeOffset occurredAt, CancellationToken ct)
    {
        // Novarad audits into year partitions (shared.audit_<YYYY>); there is no
        // router trigger, so we insert directly into the partition for this row's
        // year. id + time_stamp carry table defaults. product_id/application_id are
        // FKs to shared.products / shared.applications — mapped from the subsystem
        // we touched so our rows sit alongside Novarad's own audit for that table.
        // A failure here aborts the whole transaction, which is intended — no
        // Novarad write goes uncredited.
        var auditTable = $"{auditTableBase}_{occurredAt.UtcDateTime.Year}";
        var (productId, applicationId) = AuditIdentityFor(resourceType);
        long? referenceId = long.TryParse(resourceId, out var rid) ? rid : null;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = (NpgsqlTransaction)tx;
        cmd.CommandText = $"""
            INSERT INTO {auditTable}
                (operation, details, user_name, machine_name, machine_id,
                 product_id, application_id, context, time_stamp, reference_id)
            VALUES (@operation, @details, @user_name, @machine_name, @machine_id,
                    @product_id, @application_id, @context, @ts, @reference_id)
            """;
        cmd.Parameters.AddWithValue("operation", operation);
        cmd.Parameters.AddWithValue("details", (object?)details ?? DBNull.Value);
        cmd.Parameters.AddWithValue("user_name", actor);
        cmd.Parameters.AddWithValue("machine_name", Environment.MachineName);
        cmd.Parameters.AddWithValue("machine_id", "RadiologyPlus");
        cmd.Parameters.AddWithValue("product_id", productId);
        cmd.Parameters.AddWithValue("application_id", applicationId);
        cmd.Parameters.AddWithValue("context", (object?)resourceType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ts", occurredAt.UtcDateTime);
        cmd.Parameters.AddWithValue("reference_id", (object?)referenceId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // RadiologyPlus isn't in Novarad's product/application catalog, so we record
    // under the closest existing identity for the subsystem we wrote to. The real
    // attribution lives in user_name ('RadiologyPlus:<user>'). Values verified
    // against shared.products / shared.applications on the customer DB:
    //   product 1 = NovaPACS, app 2 = Remote Data Access (under NovaPACS)
    //   product 2 = NovaRIS,  app 9 = Remote Data Access (under NovaRIS)
    private static (int ProductId, int ApplicationId) AuditIdentityFor(string resourceType)
    {
        if (resourceType.StartsWith("pacs.", StringComparison.OrdinalIgnoreCase)) return (1, 2);
        if (resourceType.StartsWith("ris.", StringComparison.OrdinalIgnoreCase)) return (2, 9);
        return (2, 9);
    }

    private async Task<long> InsertAppAccessLogAsync(
        Guid tenantId, AppUser user, string description, string resourceType, string resourceId,
        bool success, string reason, object? before, object? after, string? error,
        DateTimeOffset occurredAt, CancellationToken ct)
    {
        await using var conn = (NpgsqlConnection)await _appDb.OpenUnscopedAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO audit.access_logs
                (tenant_id, user_id, username, action, resource_type, resource_id, success,
                 error_message, metadata, occurred_at)
            VALUES (@tenant, @user, @username, 8, @rtype, @rid, @success,
                    @error, @meta::jsonb, @ts)
            RETURNING log_id
            """;
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("user", user.UserId);
        cmd.Parameters.AddWithValue("username", user.Username);
        cmd.Parameters.AddWithValue("rtype", resourceType);
        cmd.Parameters.AddWithValue("rid", resourceId);
        cmd.Parameters.AddWithValue("success", success);
        cmd.Parameters.Add(new NpgsqlParameter("error", NpgsqlDbType.Text) { Value = (object?)error ?? DBNull.Value });
        var meta = new
        {
            description,
            reason,
            before,
            after,
        };
        cmd.Parameters.AddWithValue("meta", JsonSerializer.Serialize(meta));
        cmd.Parameters.AddWithValue("ts", occurredAt.UtcDateTime);
        var id = (long)(await cmd.ExecuteScalarAsync(ct) ?? -1L);
        return id;
    }
}
