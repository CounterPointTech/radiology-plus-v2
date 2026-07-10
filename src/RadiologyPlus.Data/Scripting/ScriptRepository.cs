using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using RadiologyPlus.Core.Data;
using RadiologyPlus.Scripting;

namespace RadiologyPlus.Data.Scripting;

public sealed class ScriptRepository : IScriptRepository
{
    private readonly IAppDbContext _db;

    public ScriptRepository(IAppDbContext db) => _db = db;

    public async Task<ScriptRecord?> GetAsync(Guid scriptId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT script_id, tenant_id, name, language, body, cron_expression, is_active, timeout_seconds, parameters_json, connection_target
            FROM scripting.scripts WHERE script_id = @id
            """;
        cmd.Parameters.AddWithValue("id", scriptId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return Map(reader);
    }

    public async Task<IReadOnlyList<ScriptRecord>> ListActiveAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT script_id, tenant_id, name, language, body, cron_expression, is_active, timeout_seconds, parameters_json, connection_target
            FROM scripting.scripts WHERE is_active
            """;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var list = new List<ScriptRecord>();
        while (await reader.ReadAsync(cancellationToken)) list.Add(Map(reader));
        return list;
    }

    public async Task<long> CreateExecutionAsync(Guid scriptId, Guid tenantId, string triggeredBy, Guid? userId,
        IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO scripting.executions
                (script_id, tenant_id, triggered_by, triggered_by_user, status, parameters_used)
            VALUES (@s, @t, @tb, @u, 'pending', @p::jsonb)
            RETURNING execution_id
            """;
        cmd.Parameters.AddWithValue("s", scriptId);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("tb", triggeredBy);
        cmd.Parameters.Add(new NpgsqlParameter("u", NpgsqlDbType.Uuid) { Value = (object?)userId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("p", parameters is null ? "{}" : JsonSerializer.Serialize(parameters));
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task MarkRunningAsync(long executionId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE scripting.executions
            SET status = 'running', started_at = NOW()
            WHERE execution_id = @id
            """;
        cmd.Parameters.AddWithValue("id", executionId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateExecutionAsync(long executionId, ScriptExecutionResult result, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE scripting.executions SET
                status = @status,
                completed_at = NOW(),
                duration_ms = @dur,
                exit_code = @exit,
                output_log = @out,
                error_log = @err,
                rows_affected = @rows
            WHERE execution_id = @id
            """;
        cmd.Parameters.AddWithValue("id", executionId);
        cmd.Parameters.AddWithValue("status", MapStatus(result.Status));
        cmd.Parameters.AddWithValue("dur", (int)Math.Min(result.DurationMs, int.MaxValue));
        cmd.Parameters.Add(new NpgsqlParameter("exit", NpgsqlDbType.Integer) { Value = (object?)result.ExitCode ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("out", NpgsqlDbType.Text) { Value = (object?)result.Output ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("err", NpgsqlDbType.Text) { Value = (object?)result.Error ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("rows", NpgsqlDbType.Integer) { Value = (object?)result.RowsAffected ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string MapStatus(ScriptExecutionStatus s) => s switch
    {
        ScriptExecutionStatus.Pending => "pending",
        ScriptExecutionStatus.Running => "running",
        ScriptExecutionStatus.Success => "success",
        ScriptExecutionStatus.Failed => "failed",
        ScriptExecutionStatus.Cancelled => "cancelled",
        ScriptExecutionStatus.Timeout => "failed",
        _ => "failed",
    };

    private static ScriptRecord Map(Npgsql.NpgsqlDataReader r)
    {
        Dictionary<string, object?>? parameters = null;
        if (!r.IsDBNull(8))
        {
            var json = r.GetString(8);
            parameters = JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
        }
        return new ScriptRecord(
            ScriptId: r.GetGuid(0),
            TenantId: r.GetGuid(1),
            Name: r.GetString(2),
            Language: ScriptLanguageParser.Parse(r.GetString(3)),
            Body: r.GetString(4),
            CronExpression: r.IsDBNull(5) ? null : r.GetString(5),
            IsActive: r.GetBoolean(6),
            TimeoutSeconds: r.GetInt32(7),
            Parameters: parameters,
            ConnectionTarget: r.GetString(9));
    }
}
