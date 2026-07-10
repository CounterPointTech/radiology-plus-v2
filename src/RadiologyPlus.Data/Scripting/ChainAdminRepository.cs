using Npgsql;
using NpgsqlTypes;
using RadiologyPlus.Core.Data;
using RadiologyPlus.Scripting;

namespace RadiologyPlus.Data.Scripting;

/// <summary>
/// Management surface over scripting.script_chains / script_chain_links / chain_runs
/// for the Chains console. Tenant-scoped: every query passes tenant_id explicitly
/// (mirrors ScriptAdminRepository).
/// </summary>
public sealed class ChainAdminRepository : IChainAdminRepository
{
    private readonly IAppDbContext _db;

    public ChainAdminRepository(IAppDbContext db) => _db = db;

    // -- List / read -----------------------------------------------------------

    public async Task<IReadOnlyList<ChainSummary>> ListAllAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        // LEFT JOIN LATERAL ... LIMIT 1 for the last-run rollup (same shape as the
        // script list — never a plain LEFT JOIN against a many-rows table).
        cmd.CommandText = """
            SELECT c.chain_id, c.name, c.description, c.on_failure, c.cron_expression, c.is_active,
                   (SELECT COUNT(*) FROM scripting.script_chain_links l WHERE l.chain_id = c.chain_id)::int AS step_count,
                   (c.notify_on_failure_recipient IS NOT NULL) AS notifies,
                   c.created_at,
                   r.chain_run_id, r.status, r.started_at, r.duration_ms
            FROM scripting.script_chains c
            LEFT JOIN LATERAL (
                SELECT chain_run_id, status, started_at, duration_ms
                FROM scripting.chain_runs
                WHERE chain_id = c.chain_id
                ORDER BY created_at DESC
                LIMIT 1
            ) r ON TRUE
            WHERE c.tenant_id = @t
            ORDER BY c.name
            """;
        cmd.Parameters.AddWithValue("t", tenantId);

        var list = new List<ChainSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new ChainSummary(
                ChainId: reader.GetGuid(0),
                Name: reader.GetString(1),
                Description: reader.IsDBNull(2) ? null : reader.GetString(2),
                OnFailure: reader.GetString(3),
                CronExpression: reader.IsDBNull(4) ? null : reader.GetString(4),
                IsActive: reader.GetBoolean(5),
                StepCount: reader.GetInt32(6),
                NotifiesOnFailure: reader.GetBoolean(7),
                CreatedAt: new DateTimeOffset(reader.GetDateTime(8), TimeSpan.Zero),
                LastRunId: reader.IsDBNull(9) ? null : reader.GetInt64(9),
                LastRunStatus: reader.IsDBNull(10) ? null : reader.GetString(10),
                LastRunStartedAt: reader.IsDBNull(11) ? null : new DateTimeOffset(reader.GetDateTime(11), TimeSpan.Zero),
                LastRunDurationMs: reader.IsDBNull(12) ? null : reader.GetInt32(12)));
        }
        return list;
    }

    public async Task<ChainDetail?> GetDetailAsync(Guid tenantId, Guid chainId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        return await GetDetailCoreAsync(conn, tenantId, chainId, cancellationToken);
    }

    private static async Task<ChainDetail?> GetDetailCoreAsync(
        NpgsqlConnection conn, Guid tenantId, Guid chainId, CancellationToken cancellationToken)
    {
        ChainDetail? header = null;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT chain_id, name, description, on_failure, cron_expression, is_active,
                       notify_on_failure_recipient, notify_on_failure_template, created_at
                FROM scripting.script_chains
                WHERE tenant_id = @t AND chain_id = @id
                """;
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("id", chainId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            header = new ChainDetail(
                ChainId: reader.GetGuid(0),
                Name: reader.GetString(1),
                Description: reader.IsDBNull(2) ? null : reader.GetString(2),
                OnFailure: reader.GetString(3),
                CronExpression: reader.IsDBNull(4) ? null : reader.GetString(4),
                IsActive: reader.GetBoolean(5),
                NotifyOnFailureRecipient: reader.IsDBNull(6) ? null : reader.GetString(6),
                NotifyOnFailureTemplateId: reader.IsDBNull(7) ? null : reader.GetGuid(7),
                CreatedAt: new DateTimeOffset(reader.GetDateTime(8), TimeSpan.Zero),
                Steps: []);
        }

        var steps = new List<ChainStepInfo>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT l.step_order, l.script_id, s.name, s.language, s.is_active, l.continue_on_failure
                FROM scripting.script_chain_links l
                JOIN scripting.scripts s ON s.script_id = l.script_id
                WHERE l.chain_id = @id
                ORDER BY l.step_order
                """;
            cmd.Parameters.AddWithValue("id", chainId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                steps.Add(new ChainStepInfo(
                    StepOrder: reader.GetInt32(0),
                    ScriptId: reader.GetGuid(1),
                    ScriptName: reader.GetString(2),
                    Language: ScriptLanguageParser.Parse(reader.GetString(3)),
                    ScriptIsActive: reader.GetBoolean(4),
                    ContinueOnFailure: reader.GetBoolean(5)));
            }
        }

        return header with { Steps = steps };
    }

    // -- Create / update / delete ------------------------------------------------

    public async Task<ChainDetail> CreateAsync(Guid tenantId, ChainSave input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        await ValidateScriptsAsync(conn, tenantId, input.Steps, cancellationToken);

        Guid chainId;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO scripting.script_chains
                    (tenant_id, name, description, on_failure, cron_expression, is_active,
                     notify_on_failure_recipient, notify_on_failure_template)
                VALUES (@t, @n, @d, @f, @cron, @active, @nr, @nt)
                RETURNING chain_id
                """;
            AddChainParams(cmd, tenantId, input);
            chainId = (Guid)(await cmd.ExecuteScalarAsync(cancellationToken))!;
        }

        await InsertLinksAsync(conn, chainId, input.Steps, cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return (await GetDetailCoreAsync(conn, tenantId, chainId, cancellationToken))!;
    }

    public async Task<ChainDetail> UpdateAsync(Guid tenantId, Guid chainId, ChainSave input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        await ValidateScriptsAsync(conn, tenantId, input.Steps, cancellationToken);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE scripting.script_chains
                SET name = @n, description = @d, on_failure = @f, cron_expression = @cron,
                    is_active = @active, notify_on_failure_recipient = @nr, notify_on_failure_template = @nt
                WHERE tenant_id = @t AND chain_id = @id
                """;
            AddChainParams(cmd, tenantId, input);
            cmd.Parameters.AddWithValue("id", chainId);
            if (await cmd.ExecuteNonQueryAsync(cancellationToken) == 0)
                throw new KeyNotFoundException($"Chain {chainId} not found for tenant {tenantId}.");
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM scripting.script_chain_links WHERE chain_id = @id";
            cmd.Parameters.AddWithValue("id", chainId);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertLinksAsync(conn, chainId, input.Steps, cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return (await GetDetailCoreAsync(conn, tenantId, chainId, cancellationToken))!;
    }

    public async Task<ChainDetail> SetActiveAsync(Guid tenantId, Guid chainId, bool isActive, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE scripting.script_chains
            SET is_active = @active
            WHERE tenant_id = @t AND chain_id = @id
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("id", chainId);
        cmd.Parameters.AddWithValue("active", isActive);
        if (await cmd.ExecuteNonQueryAsync(cancellationToken) == 0)
            throw new KeyNotFoundException($"Chain {chainId} not found for tenant {tenantId}.");
        return (await GetDetailCoreAsync(conn, tenantId, chainId, cancellationToken))!;
    }

    public async Task<bool> DeleteAsync(Guid tenantId, Guid chainId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        // Links + chain_runs cascade; executions keep their rows (chain_run_id -> NULL).
        cmd.CommandText = "DELETE FROM scripting.script_chains WHERE tenant_id = @t AND chain_id = @id";
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("id", chainId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static void AddChainParams(NpgsqlCommand cmd, Guid tenantId, ChainSave input)
    {
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("n", input.Name);
        cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Text) { Value = (object?)input.Description ?? DBNull.Value });
        cmd.Parameters.AddWithValue("f", input.OnFailure);
        cmd.Parameters.Add(new NpgsqlParameter("cron", NpgsqlDbType.Text) { Value = (object?)input.CronExpression ?? DBNull.Value });
        cmd.Parameters.AddWithValue("active", input.IsActive);
        cmd.Parameters.Add(new NpgsqlParameter("nr", NpgsqlDbType.Text) { Value = (object?)input.NotifyOnFailureRecipient ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("nt", NpgsqlDbType.Uuid) { Value = (object?)input.NotifyOnFailureTemplateId ?? DBNull.Value });
    }

    private static async Task ValidateScriptsAsync(
        NpgsqlConnection conn, Guid tenantId, IReadOnlyList<ChainStepSave> steps, CancellationToken cancellationToken)
    {
        var distinctIds = steps.Select(s => s.ScriptId).Distinct().ToArray();
        if (distinctIds.Length == 0) return;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)::int FROM scripting.scripts
            WHERE tenant_id = @t AND script_id = ANY(@ids)
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("ids", distinctIds);
        var found = (int)(await cmd.ExecuteScalarAsync(cancellationToken))!;
        if (found != distinctIds.Length)
            throw new InvalidOperationException("One or more steps reference a script that doesn't exist.");
    }

    private static async Task InsertLinksAsync(
        NpgsqlConnection conn, Guid chainId, IReadOnlyList<ChainStepSave> steps, CancellationToken cancellationToken)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO scripting.script_chain_links (chain_id, step_order, script_id, continue_on_failure)
                VALUES (@c, @o, @s, @cf)
                """;
            cmd.Parameters.AddWithValue("c", chainId);
            cmd.Parameters.AddWithValue("o", i + 1);
            cmd.Parameters.AddWithValue("s", steps[i].ScriptId);
            cmd.Parameters.AddWithValue("cf", steps[i].ContinueOnFailure);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    // -- Runs ----------------------------------------------------------------------

    public async Task<IReadOnlyList<ChainRunInfo>> ListRunsAsync(
        Guid tenantId, Guid? chainId, int limit, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.chain_run_id, r.chain_id, c.name, r.triggered_by, r.status,
                   r.started_at, r.completed_at, r.duration_ms,
                   r.steps_total, r.steps_succeeded, r.steps_failed, r.error_summary, r.created_at
            FROM scripting.chain_runs r
            JOIN scripting.script_chains c ON c.chain_id = r.chain_id
            WHERE r.tenant_id = @t
              AND (@c::uuid IS NULL OR r.chain_id = @c)
            ORDER BY r.created_at DESC, r.chain_run_id DESC
            LIMIT @lim
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Uuid) { Value = (object?)chainId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("lim", limit);

        var list = new List<ChainRunInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            list.Add(ReadRunInfo(reader));
        return list;
    }

    public async Task<ChainRunDetail?> GetRunAsync(Guid tenantId, long chainRunId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);

        ChainRunInfo? run = null;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT r.chain_run_id, r.chain_id, c.name, r.triggered_by, r.status,
                       r.started_at, r.completed_at, r.duration_ms,
                       r.steps_total, r.steps_succeeded, r.steps_failed, r.error_summary, r.created_at
                FROM scripting.chain_runs r
                JOIN scripting.script_chains c ON c.chain_id = r.chain_id
                WHERE r.tenant_id = @t AND r.chain_run_id = @id
                """;
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("id", chainRunId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            run = ReadRunInfo(reader);
        }

        var steps = new List<ChainRunStep>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT e.execution_id, e.script_id, s.name, e.status,
                       e.started_at, e.completed_at, e.duration_ms, e.rows_affected
                FROM scripting.executions e
                JOIN scripting.scripts s ON s.script_id = e.script_id
                WHERE e.chain_run_id = @id
                ORDER BY e.execution_id
                """;
            cmd.Parameters.AddWithValue("id", chainRunId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                steps.Add(new ChainRunStep(
                    ExecutionId: reader.GetInt64(0),
                    ScriptId: reader.GetGuid(1),
                    ScriptName: reader.GetString(2),
                    Status: reader.GetString(3),
                    StartedAt: reader.IsDBNull(4) ? null : new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero),
                    CompletedAt: reader.IsDBNull(5) ? null : new DateTimeOffset(reader.GetDateTime(5), TimeSpan.Zero),
                    DurationMs: reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    RowsAffected: reader.IsDBNull(7) ? null : reader.GetInt32(7)));
            }
        }

        return new ChainRunDetail(run, steps);
    }

    private static ChainRunInfo ReadRunInfo(NpgsqlDataReader reader) => new(
        ChainRunId: reader.GetInt64(0),
        ChainId: reader.GetGuid(1),
        ChainName: reader.GetString(2),
        TriggeredBy: reader.GetString(3),
        Status: reader.GetString(4),
        StartedAt: reader.IsDBNull(5) ? null : new DateTimeOffset(reader.GetDateTime(5), TimeSpan.Zero),
        CompletedAt: reader.IsDBNull(6) ? null : new DateTimeOffset(reader.GetDateTime(6), TimeSpan.Zero),
        DurationMs: reader.IsDBNull(7) ? null : reader.GetInt32(7),
        StepsTotal: reader.GetInt32(8),
        StepsSucceeded: reader.GetInt32(9),
        StepsFailed: reader.GetInt32(10),
        ErrorSummary: reader.IsDBNull(11) ? null : reader.GetString(11),
        CreatedAt: new DateTimeOffset(reader.GetDateTime(12), TimeSpan.Zero));
}
