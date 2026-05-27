using Npgsql;
using NpgsqlTypes;
using RadiologyPlus.Core.Data;
using RadiologyPlus.Notifications;

namespace RadiologyPlus.Data.Notifications;

public sealed class NotificationRepository : INotificationRepository
{
    private readonly IAppDbContext _db;

    public NotificationRepository(IAppDbContext db) => _db = db;

    public async Task<long> EnqueueAsync(NotificationRequest request, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO notifications.queue
                (tenant_id, template_id, channel, recipient, subject, body, is_html, priority,
                 status, retry_count, max_retries, scheduled_at, source_type, source_id)
            VALUES (@t, @tmpl, @ch, @r, @s, @b, @h, @p, 'pending', 0, 3, NOW(), @st, @si)
            RETURNING notification_id
            """;
        cmd.Parameters.AddWithValue("t", request.TenantId);
        cmd.Parameters.Add(new NpgsqlParameter("tmpl", NpgsqlDbType.Uuid) { Value = (object?)request.TemplateId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("ch", MapChannel(request.Channel));
        cmd.Parameters.AddWithValue("r", request.Recipient);
        cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Text) { Value = (object?)request.Subject ?? DBNull.Value });
        cmd.Parameters.AddWithValue("b", request.Body);
        cmd.Parameters.AddWithValue("h", request.IsHtml);
        cmd.Parameters.AddWithValue("p", (short)request.Priority);
        cmd.Parameters.Add(new NpgsqlParameter("st", NpgsqlDbType.Text) { Value = (object?)request.SourceType ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("si", NpgsqlDbType.Text) { Value = (object?)request.SourceId ?? DBNull.Value });
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<IReadOnlyList<QueuedNotification>> DequeueBatchAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT notification_id, tenant_id, template_id, channel, recipient, subject, body, is_html,
                   priority, status, retry_count, max_retries, scheduled_at, sent_at, last_error
            FROM notifications.queue
            WHERE status = 'pending' AND scheduled_at <= NOW()
            ORDER BY priority ASC, scheduled_at ASC
            LIMIT @lim
            """;
        cmd.Parameters.AddWithValue("lim", batchSize);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var list = new List<QueuedNotification>();
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new QueuedNotification(
                NotificationId: reader.GetInt64(0),
                TenantId: reader.GetGuid(1),
                TemplateId: reader.IsDBNull(2) ? null : reader.GetGuid(2),
                Channel: ParseChannel(reader.GetString(3)),
                Recipient: reader.GetString(4),
                Subject: reader.IsDBNull(5) ? null : reader.GetString(5),
                Body: reader.GetString(6),
                IsHtml: reader.GetBoolean(7),
                Priority: reader.GetInt16(8),
                Status: ParseStatus(reader.GetString(9)),
                RetryCount: reader.GetInt32(10),
                MaxRetries: reader.GetInt32(11),
                ScheduledAt: new DateTimeOffset(reader.GetDateTime(12), TimeSpan.Zero),
                SentAt: reader.IsDBNull(13) ? null : new DateTimeOffset(reader.GetDateTime(13), TimeSpan.Zero),
                LastError: reader.IsDBNull(14) ? null : reader.GetString(14)));
        }
        return list;
    }

    public async Task MarkSendingAsync(long notificationId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE notifications.queue SET status = 'sending' WHERE notification_id = @id";
        cmd.Parameters.AddWithValue("id", notificationId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkSentAsync(long notificationId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE notifications.queue SET status = 'sent', sent_at = NOW() WHERE notification_id = @id";
        cmd.Parameters.AddWithValue("id", notificationId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(long notificationId, string errorMessage, bool shouldRetry, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        // If retries are exhausted, mark failed. Otherwise put back as pending with a backoff delay.
        cmd.CommandText = shouldRetry
            ? """
              UPDATE notifications.queue
              SET status = 'pending',
                  retry_count = retry_count + 1,
                  scheduled_at = NOW() + (POWER(2, LEAST(retry_count, 6)) * INTERVAL '30 seconds'),
                  last_error = @err
              WHERE notification_id = @id
              """
            : """
              UPDATE notifications.queue
              SET status = 'failed',
                  failed_at = NOW(),
                  last_error = @err
              WHERE notification_id = @id
              """;
        cmd.Parameters.AddWithValue("id", notificationId);
        cmd.Parameters.AddWithValue("err", errorMessage);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string MapChannel(NotificationChannel c) => c switch
    {
        NotificationChannel.Email => "email",
        NotificationChannel.Teams => "teams",
        NotificationChannel.Sms => "sms",
        NotificationChannel.Webhook => "webhook",
        _ => "email",
    };

    private static NotificationChannel ParseChannel(string s) => s switch
    {
        "email" => NotificationChannel.Email,
        "teams" => NotificationChannel.Teams,
        "sms" => NotificationChannel.Sms,
        "webhook" => NotificationChannel.Webhook,
        _ => NotificationChannel.Email,
    };

    private static NotificationStatus ParseStatus(string s) => s switch
    {
        "pending" => NotificationStatus.Pending,
        "sending" => NotificationStatus.Sending,
        "sent" => NotificationStatus.Sent,
        "failed" => NotificationStatus.Failed,
        "cancelled" => NotificationStatus.Cancelled,
        _ => NotificationStatus.Pending,
    };
}
