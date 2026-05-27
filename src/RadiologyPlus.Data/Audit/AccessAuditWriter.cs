using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;
using RadiologyPlus.Core.Audit;
using RadiologyPlus.Core.Data;
using RadiologyPlus.Core.Identity;

namespace RadiologyPlus.Data.Audit;

public sealed class AccessAuditWriter : IAccessAuditWriter
{
    private readonly IAppDbContext _db;

    public AccessAuditWriter(IAppDbContext db) => _db = db;

    public Task WriteSuccessAsync(Guid tenantId, AppUser user, AccessAction action, string description,
        HttpContext http, CancellationToken cancellationToken = default)
        => WriteAsync(tenantId, user.UserId, user.Username, action, "auth", null, true, null, description, http, cancellationToken);

    public Task WriteFailureAsync(Guid tenantId, string? username, AccessAction action, string reason,
        HttpContext http, CancellationToken cancellationToken = default)
        => WriteAsync(tenantId, null, username, action, "auth", null, false, reason, reason, http, cancellationToken);

    private async Task WriteAsync(
        Guid tenantId, Guid? userId, string? username, AccessAction action,
        string resourceType, string? resourceId, bool success, string? error, string description,
        HttpContext http, CancellationToken ct)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO audit.access_logs
                (tenant_id, user_id, username, action, resource_type, resource_id, success,
                 ip_address, user_agent, error_message, metadata, occurred_at)
            VALUES (@tenant, @user, @username, @action, @rtype, @rid, @success,
                    @ip, @ua, @error, @meta::jsonb, NOW())
            """;
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.Add(new NpgsqlParameter("user", NpgsqlDbType.Uuid) { Value = (object?)userId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("username", NpgsqlDbType.Text) { Value = (object?)username ?? DBNull.Value });
        cmd.Parameters.AddWithValue("action", (short)action);
        cmd.Parameters.AddWithValue("rtype", resourceType);
        cmd.Parameters.Add(new NpgsqlParameter("rid", NpgsqlDbType.Text) { Value = (object?)resourceId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("success", success);

        var ip = ExtractIp(http);
        cmd.Parameters.Add(new NpgsqlParameter("ip", NpgsqlDbType.Inet) { Value = (object?)ip ?? DBNull.Value });
        var ua = http.Request.Headers.UserAgent.ToString();
        cmd.Parameters.Add(new NpgsqlParameter("ua", NpgsqlDbType.Text) { Value = string.IsNullOrEmpty(ua) ? DBNull.Value : ua });
        cmd.Parameters.Add(new NpgsqlParameter("error", NpgsqlDbType.Text) { Value = (object?)error ?? DBNull.Value });
        cmd.Parameters.AddWithValue("meta", JsonSerializer.Serialize(new { description }));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static IPAddress? ExtractIp(HttpContext http)
    {
        var forwarded = http.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            var first = forwarded.Split(',')[0].Trim();
            if (IPAddress.TryParse(first, out var fwd)) return fwd;
        }
        return http.Connection.RemoteIpAddress;
    }
}
