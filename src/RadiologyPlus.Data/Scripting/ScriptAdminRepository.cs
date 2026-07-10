using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using RadiologyPlus.Core.Data;
using RadiologyPlus.Scripting;

namespace RadiologyPlus.Data.Scripting;

/// <summary>
/// Script Manager data access (list/edit/versions/executions). Tenant-scoped —
/// unlike the engine-facing ScriptRepository, every statement passes tenant_id
/// explicitly and opens a tenant-scoped connection (RLS enforces it as well).
/// </summary>
public sealed class ScriptAdminRepository : IScriptAdminRepository
{
    private readonly IAppDbContext _db;

    public ScriptAdminRepository(IAppDbContext db) => _db = db;

    public async Task<IReadOnlyList<ScriptSummary>> ListAllAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        // LEFT JOIN LATERAL ... LIMIT 1 for the last-run rollup (never a plain join —
        // executions fan out per script).
        cmd.CommandText = """
            SELECT s.script_id, s.name, s.description, s.language, s.connection_target,
                   s.cron_expression, s.is_active, s.timeout_seconds, s.created_at, s.updated_at,
                   le.execution_id, le.status, le.started_at, le.duration_ms
            FROM scripting.scripts s
            LEFT JOIN LATERAL (
                SELECT e.execution_id, e.status, e.started_at, e.duration_ms
                FROM scripting.executions e
                WHERE e.script_id = s.script_id
                ORDER BY e.execution_id DESC
                LIMIT 1
            ) le ON TRUE
            WHERE s.tenant_id = @t
            ORDER BY s.name
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var list = new List<ScriptSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new ScriptSummary(
                ScriptId: reader.GetGuid(0),
                Name: reader.GetString(1),
                Description: reader.IsDBNull(2) ? null : reader.GetString(2),
                Language: ScriptLanguageParser.Parse(reader.GetString(3)),
                ConnectionTarget: reader.GetString(4),
                CronExpression: reader.IsDBNull(5) ? null : reader.GetString(5),
                IsActive: reader.GetBoolean(6),
                TimeoutSeconds: reader.GetInt32(7),
                CreatedAt: Ts(reader, 8),
                UpdatedAt: Ts(reader, 9),
                LastExecutionId: reader.IsDBNull(10) ? null : reader.GetInt64(10),
                LastStatus: reader.IsDBNull(11) ? null : reader.GetString(11),
                LastStartedAt: reader.IsDBNull(12) ? null : Ts(reader, 12),
                LastDurationMs: reader.IsDBNull(13) ? null : reader.GetInt32(13)));
        }
        return list;
    }

    public async Task<ScriptDetail?> GetDetailAsync(Guid tenantId, Guid scriptId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {DetailColumns}
            FROM scripting.scripts
            WHERE tenant_id = @t AND script_id = @id
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("id", scriptId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return MapDetail(reader);
    }

    public async Task<ScriptDetail> CreateScriptAsync(Guid tenantId, Guid? createdBy, ScriptCreate input, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO scripting.scripts
                (tenant_id, name, description, language, body, connection_target,
                 cron_expression, is_active, timeout_seconds, parameters_json, created_by)
            VALUES (@t, @name, @desc, @lang, @body, @target, @cron, @active, @timeout, @params::jsonb, @by)
            RETURNING {DetailColumns}
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        AddScriptParameters(cmd, input.Name, input.Description, input.Language, input.Body,
            input.ConnectionTarget, input.CronExpression, input.IsActive, input.TimeoutSeconds, input.Parameters);
        cmd.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)createdBy ?? DBNull.Value });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return MapDetail(reader);
    }

    public async Task<ScriptUpdateResult> UpdateScriptAsync(Guid tenantId, Guid scriptId, Guid? savedBy, ScriptUpdate input, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        // Capture the current row (Before) and lock it for the update.
        ScriptDetail before;
        await using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = $"""
                SELECT {DetailColumns}
                FROM scripting.scripts
                WHERE tenant_id = @t AND script_id = @id
                FOR UPDATE
                """;
            read.Parameters.AddWithValue("t", tenantId);
            read.Parameters.AddWithValue("id", scriptId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new KeyNotFoundException($"Script {scriptId} not found.");
            before = MapDetail(reader);
        }

        // Body changed -> snapshot the OLD body into script_versions first.
        if (!string.Equals(before.Body, input.Body, StringComparison.Ordinal))
        {
            await using var snap = conn.CreateCommand();
            snap.Transaction = tx;
            snap.CommandText = """
                INSERT INTO scripting.script_versions (script_id, tenant_id, version_number, body, saved_by)
                SELECT @id, @t,
                       COALESCE((SELECT MAX(version_number) FROM scripting.script_versions WHERE script_id = @id), 0) + 1,
                       @body, @by
                """;
            snap.Parameters.AddWithValue("id", scriptId);
            snap.Parameters.AddWithValue("t", tenantId);
            snap.Parameters.AddWithValue("body", before.Body);
            snap.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)savedBy ?? DBNull.Value });
            await snap.ExecuteNonQueryAsync(cancellationToken);
        }

        ScriptDetail after;
        await using (var update = conn.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = $"""
                UPDATE scripting.scripts SET
                    name = @name,
                    description = @desc,
                    language = @lang,
                    body = @body,
                    connection_target = @target,
                    cron_expression = @cron,
                    is_active = @active,
                    timeout_seconds = @timeout,
                    parameters_json = @params::jsonb,
                    updated_at = NOW()
                WHERE tenant_id = @t AND script_id = @id
                RETURNING {DetailColumns}
                """;
            update.Parameters.AddWithValue("t", tenantId);
            update.Parameters.AddWithValue("id", scriptId);
            AddScriptParameters(update, input.Name, input.Description, input.Language, input.Body,
                input.ConnectionTarget, input.CronExpression, input.IsActive, input.TimeoutSeconds, input.Parameters);
            await using var reader = await update.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            after = MapDetail(reader);
        }

        await tx.CommitAsync(cancellationToken);
        return new ScriptUpdateResult(before, after);
    }

    public async Task<ScriptDetail> SetScriptActiveAsync(Guid tenantId, Guid scriptId, bool isActive, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE scripting.scripts
            SET is_active = @active, updated_at = NOW()
            WHERE tenant_id = @t AND script_id = @id
            RETURNING {DetailColumns}
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("id", scriptId);
        cmd.Parameters.AddWithValue("active", isActive);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new KeyNotFoundException($"Script {scriptId} not found.");
        return MapDetail(reader);
    }

    public async Task<bool> DeleteScriptAsync(Guid tenantId, Guid scriptId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM scripting.scripts WHERE tenant_id = @t AND script_id = @id";
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("id", scriptId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<IReadOnlyList<ScriptExecutionListItem>> ListExecutionsAsync(Guid tenantId, Guid? scriptId, int limit, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        var scriptFilter = scriptId is null ? "" : "AND e.script_id = @sid";
        cmd.CommandText = $"""
            SELECT e.execution_id, e.script_id, s.name, e.triggered_by, e.status,
                   e.started_at, e.completed_at, e.duration_ms, e.rows_affected, e.created_at
            FROM scripting.executions e
            JOIN scripting.scripts s ON s.script_id = e.script_id
            WHERE e.tenant_id = @t {scriptFilter}
            ORDER BY e.execution_id DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        if (scriptId is not null) cmd.Parameters.AddWithValue("sid", scriptId.Value);
        cmd.Parameters.AddWithValue("limit", limit);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var list = new List<ScriptExecutionListItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new ScriptExecutionListItem(
                ExecutionId: reader.GetInt64(0),
                ScriptId: reader.GetGuid(1),
                ScriptName: reader.GetString(2),
                TriggeredBy: reader.GetString(3),
                Status: reader.GetString(4),
                StartedAt: reader.IsDBNull(5) ? null : Ts(reader, 5),
                CompletedAt: reader.IsDBNull(6) ? null : Ts(reader, 6),
                DurationMs: reader.IsDBNull(7) ? null : reader.GetInt32(7),
                RowsAffected: reader.IsDBNull(8) ? null : reader.GetInt32(8),
                CreatedAt: Ts(reader, 9)));
        }
        return list;
    }

    public async Task<ScriptExecutionDetail?> GetExecutionAsync(Guid tenantId, long executionId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT e.execution_id, e.script_id, s.name, e.triggered_by, e.status,
                   e.started_at, e.completed_at, e.duration_ms, e.exit_code,
                   e.output_log, e.error_log, e.rows_affected, e.created_at
            FROM scripting.executions e
            JOIN scripting.scripts s ON s.script_id = e.script_id
            WHERE e.tenant_id = @t AND e.execution_id = @id
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("id", executionId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ScriptExecutionDetail(
            ExecutionId: reader.GetInt64(0),
            ScriptId: reader.GetGuid(1),
            ScriptName: reader.GetString(2),
            TriggeredBy: reader.GetString(3),
            Status: reader.GetString(4),
            StartedAt: reader.IsDBNull(5) ? null : Ts(reader, 5),
            CompletedAt: reader.IsDBNull(6) ? null : Ts(reader, 6),
            DurationMs: reader.IsDBNull(7) ? null : reader.GetInt32(7),
            ExitCode: reader.IsDBNull(8) ? null : reader.GetInt32(8),
            OutputLog: reader.IsDBNull(9) ? null : reader.GetString(9),
            ErrorLog: reader.IsDBNull(10) ? null : reader.GetString(10),
            RowsAffected: reader.IsDBNull(11) ? null : reader.GetInt32(11),
            CreatedAt: Ts(reader, 12));
    }

    public async Task<IReadOnlyList<ScriptVersionInfo>> ListVersionsAsync(Guid tenantId, Guid scriptId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT version_id, script_id, version_number, length(body), saved_by, saved_at
            FROM scripting.script_versions
            WHERE tenant_id = @t AND script_id = @id
            ORDER BY version_number DESC
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("id", scriptId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var list = new List<ScriptVersionInfo>();
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new ScriptVersionInfo(
                VersionId: reader.GetGuid(0),
                ScriptId: reader.GetGuid(1),
                VersionNumber: reader.GetInt32(2),
                BodyChars: reader.GetInt32(3),
                SavedBy: reader.IsDBNull(4) ? null : reader.GetGuid(4),
                SavedAt: Ts(reader, 5)));
        }
        return list;
    }

    public async Task<ScriptVersionDetail?> GetVersionAsync(Guid tenantId, Guid versionId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT version_id, script_id, version_number, body, saved_by, saved_at
            FROM scripting.script_versions
            WHERE tenant_id = @t AND version_id = @id
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("id", versionId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ScriptVersionDetail(
            VersionId: reader.GetGuid(0),
            ScriptId: reader.GetGuid(1),
            VersionNumber: reader.GetInt32(2),
            Body: reader.GetString(3),
            SavedBy: reader.IsDBNull(4) ? null : reader.GetGuid(4),
            SavedAt: Ts(reader, 5));
    }

    // -----------------------------------------------------------------------

    private const string DetailColumns =
        "script_id, name, description, language, body, connection_target, cron_expression, " +
        "is_active, timeout_seconds, parameters_json, created_by, created_at, updated_at";

    private static void AddScriptParameters(
        NpgsqlCommand cmd, string name, string? description, ScriptLanguage language, string body,
        string connectionTarget, string? cron, bool isActive, int timeoutSeconds,
        IReadOnlyDictionary<string, object?>? parameters)
    {
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("target", connectionTarget);
        cmd.Parameters.Add(new NpgsqlParameter("desc", NpgsqlDbType.Text) { Value = (object?)description ?? DBNull.Value });
        cmd.Parameters.AddWithValue("lang", language.ToDbToken());
        cmd.Parameters.AddWithValue("body", body);
        cmd.Parameters.Add(new NpgsqlParameter("cron", NpgsqlDbType.Text) { Value = (object?)cron ?? DBNull.Value });
        cmd.Parameters.AddWithValue("active", isActive);
        cmd.Parameters.AddWithValue("timeout", timeoutSeconds);
        cmd.Parameters.Add(new NpgsqlParameter("params", NpgsqlDbType.Jsonb)
        {
            Value = parameters is null ? DBNull.Value : JsonSerializer.Serialize(parameters),
        });
    }

    private static ScriptDetail MapDetail(NpgsqlDataReader r)
    {
        Dictionary<string, object?>? parameters = null;
        if (!r.IsDBNull(9))
        {
            parameters = JsonSerializer.Deserialize<Dictionary<string, object?>>(r.GetString(9));
        }
        return new ScriptDetail(
            ScriptId: r.GetGuid(0),
            Name: r.GetString(1),
            Description: r.IsDBNull(2) ? null : r.GetString(2),
            Language: ScriptLanguageParser.Parse(r.GetString(3)),
            Body: r.GetString(4),
            ConnectionTarget: r.GetString(5),
            CronExpression: r.IsDBNull(6) ? null : r.GetString(6),
            IsActive: r.GetBoolean(7),
            TimeoutSeconds: r.GetInt32(8),
            Parameters: parameters,
            CreatedBy: r.IsDBNull(10) ? null : r.GetGuid(10),
            CreatedAt: Ts(r, 11),
            UpdatedAt: Ts(r, 12));
    }

    private static DateTimeOffset Ts(NpgsqlDataReader r, int ordinal)
        => new(r.GetDateTime(ordinal), TimeSpan.Zero);
}
