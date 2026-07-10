using Npgsql;
using NpgsqlTypes;
using RadiologyPlus.Core.Audit;
using RadiologyPlus.Core.Data;

namespace RadiologyPlus.Data.Audit;

/// <summary>
/// Read surface over audit.access_logs for the console's audit viewer.
/// Tenant-scoped: every query passes tenant_id explicitly.
/// </summary>
public sealed class AccessAuditReader : IAccessAuditReader
{
    private readonly IAppDbContext _db;

    public AccessAuditReader(IAppDbContext db) => _db = db;

    public async Task<AccessLogPage> ListAsync(Guid tenantId, AccessLogFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);

        const string where = """
            WHERE tenant_id = @t
              AND (@username::text IS NULL OR username ILIKE '%' || @username || '%')
              AND (@action::smallint IS NULL OR action = @action)
              AND (@success::boolean IS NULL OR success = @success)
              AND (@from::timestamptz IS NULL OR occurred_at >= @from)
              AND (@to::timestamptz IS NULL OR occurred_at < @to)
            """;

        int total;
        await using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(*)::int FROM audit.access_logs {where}";
            AddFilterParams(countCmd, tenantId, filter);
            total = (int)(await countCmd.ExecuteScalarAsync(cancellationToken))!;
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT log_id, user_id, username, action, resource_type, resource_id, success,
                   ip_address, user_agent, error_message, metadata, occurred_at
            FROM audit.access_logs
            {where}
            ORDER BY occurred_at DESC, log_id DESC
            LIMIT @lim OFFSET @off
            """;
        AddFilterParams(cmd, tenantId, filter);
        cmd.Parameters.AddWithValue("lim", filter.Limit);
        cmd.Parameters.AddWithValue("off", filter.Offset);

        var items = new List<AccessLogRow>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new AccessLogRow(
                LogId: reader.GetInt64(0),
                UserId: reader.IsDBNull(1) ? null : reader.GetGuid(1),
                Username: reader.IsDBNull(2) ? null : reader.GetString(2),
                Action: (AccessAction)reader.GetInt16(3),
                ResourceType: reader.GetString(4),
                ResourceId: reader.IsDBNull(5) ? null : reader.GetString(5),
                Success: reader.GetBoolean(6),
                IpAddress: reader.IsDBNull(7) ? null : reader.GetFieldValue<System.Net.IPAddress>(7).ToString(),
                UserAgent: reader.IsDBNull(8) ? null : reader.GetString(8),
                ErrorMessage: reader.IsDBNull(9) ? null : reader.GetString(9),
                MetadataJson: reader.IsDBNull(10) ? null : reader.GetString(10),
                OccurredAt: new DateTimeOffset(reader.GetDateTime(11), TimeSpan.Zero)));
        }

        return new AccessLogPage(items, total);
    }

    private static void AddFilterParams(NpgsqlCommand cmd, Guid tenantId, AccessLogFilter filter)
    {
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.Add(new NpgsqlParameter("username", NpgsqlDbType.Text)
        {
            Value = (object?)filter.Username ?? DBNull.Value,
        });
        cmd.Parameters.Add(new NpgsqlParameter("action", NpgsqlDbType.Smallint)
        {
            Value = filter.Action is { } a ? (short)a : DBNull.Value,
        });
        cmd.Parameters.Add(new NpgsqlParameter("success", NpgsqlDbType.Boolean)
        {
            Value = (object?)filter.Success ?? DBNull.Value,
        });
        cmd.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz)
        {
            Value = (object?)filter.From ?? DBNull.Value,
        });
        cmd.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz)
        {
            Value = (object?)filter.To ?? DBNull.Value,
        });
    }
}
