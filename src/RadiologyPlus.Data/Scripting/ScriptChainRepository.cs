using Npgsql;
using NpgsqlTypes;
using RadiologyPlus.Core.Data;
using RadiologyPlus.Scripting;

namespace RadiologyPlus.Data.Scripting;

/// <summary>
/// Runner-side persistence for chains + chain runs. Unscoped, like ScriptRepository —
/// the scheduler and runner work across tenants.
/// </summary>
public sealed class ScriptChainRepository : IScriptChainRepository
{
    private readonly IAppDbContext _db;

    public ScriptChainRepository(IAppDbContext db) => _db = db;

    private const string ChainColumns = """
        c.chain_id, c.tenant_id, c.name, c.on_failure, c.cron_expression, c.is_active,
        c.notify_on_failure_recipient, c.notify_on_failure_template
        """;

    public async Task<ChainRecord?> GetAsync(Guid chainId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        var chains = await ReadChainsAsync(conn, "WHERE c.chain_id = @id",
            cmd => cmd.Parameters.AddWithValue("id", chainId), cancellationToken);
        return chains.Count == 0 ? null : chains[0];
    }

    public async Task<IReadOnlyList<ChainRecord>> ListActiveAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        return await ReadChainsAsync(conn, "WHERE c.is_active", _ => { }, cancellationToken);
    }

    private static async Task<List<ChainRecord>> ReadChainsAsync(
        NpgsqlConnection conn, string where, Action<NpgsqlCommand> bind, CancellationToken cancellationToken)
    {
        // Links come back as ordered arrays per chain — one round trip, no N+1.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {ChainColumns},
                   COALESCE(ARRAY_AGG(l.step_order ORDER BY l.step_order) FILTER (WHERE l.script_id IS NOT NULL), ARRAY[]::int[]) AS step_orders,
                   COALESCE(ARRAY_AGG(l.script_id ORDER BY l.step_order) FILTER (WHERE l.script_id IS NOT NULL), ARRAY[]::uuid[]) AS script_ids,
                   COALESCE(ARRAY_AGG(l.continue_on_failure ORDER BY l.step_order) FILTER (WHERE l.script_id IS NOT NULL), ARRAY[]::boolean[]) AS continue_flags
            FROM scripting.script_chains c
            LEFT JOIN scripting.script_chain_links l ON l.chain_id = c.chain_id
            {where}
            GROUP BY c.chain_id
            """;
        bind(cmd);

        var list = new List<ChainRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var stepOrders = (int[])reader.GetValue(8);
            var scriptIds = (Guid[])reader.GetValue(9);
            var continueFlags = (bool[])reader.GetValue(10);
            var links = new List<ChainLinkRecord>(stepOrders.Length);
            for (var i = 0; i < stepOrders.Length; i++)
                links.Add(new ChainLinkRecord(stepOrders[i], scriptIds[i], continueFlags[i]));

            list.Add(new ChainRecord(
                ChainId: reader.GetGuid(0),
                TenantId: reader.GetGuid(1),
                Name: reader.GetString(2),
                OnFailure: reader.GetString(3),
                CronExpression: reader.IsDBNull(4) ? null : reader.GetString(4),
                IsActive: reader.GetBoolean(5),
                NotifyOnFailureRecipient: reader.IsDBNull(6) ? null : reader.GetString(6),
                NotifyOnFailureTemplateId: reader.IsDBNull(7) ? null : reader.GetGuid(7),
                Links: links));
        }
        return list;
    }

    public async Task<long> CreateChainRunAsync(
        Guid chainId, Guid tenantId, string triggeredBy, Guid? userId, int stepsTotal,
        CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO scripting.chain_runs
                (chain_id, tenant_id, triggered_by, triggered_by_user, status, steps_total)
            VALUES (@c, @t, @tb, @u, 'pending', @steps)
            RETURNING chain_run_id
            """;
        cmd.Parameters.AddWithValue("c", chainId);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("tb", triggeredBy);
        cmd.Parameters.Add(new NpgsqlParameter("u", NpgsqlDbType.Uuid) { Value = (object?)userId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("steps", stepsTotal);
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task MarkChainRunRunningAsync(long chainRunId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE scripting.chain_runs
            SET status = 'running', started_at = NOW()
            WHERE chain_run_id = @id
            """;
        cmd.Parameters.AddWithValue("id", chainRunId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CompleteChainRunAsync(
        long chainRunId, string status, int stepsSucceeded, int stepsFailed, string? errorSummary,
        CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE scripting.chain_runs
            SET status = @status,
                completed_at = NOW(),
                duration_ms = (EXTRACT(EPOCH FROM (NOW() - started_at)) * 1000)::int,
                steps_succeeded = @ok,
                steps_failed = @failed,
                error_summary = @err
            WHERE chain_run_id = @id
            """;
        cmd.Parameters.AddWithValue("id", chainRunId);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("ok", stepsSucceeded);
        cmd.Parameters.AddWithValue("failed", stepsFailed);
        cmd.Parameters.Add(new NpgsqlParameter("err", NpgsqlDbType.Text) { Value = (object?)errorSummary ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
