using Npgsql;
using NpgsqlTypes;
using RadiologyPlus.Core.Data;
using RadiologyPlus.Notifications;

namespace RadiologyPlus.Data.Notifications;

/// <summary>
/// Management surface over notifications.queue / notifications.templates for the
/// Notifications console. Tenant-scoped: every query passes tenant_id explicitly
/// (mirrors ScriptAdminRepository; the orchestrator's NotificationRepository stays lean).
/// </summary>
public sealed class NotificationAdminRepository : INotificationAdminRepository
{
    private readonly IAppDbContext _db;

    public NotificationAdminRepository(IAppDbContext db) => _db = db;

    // -- Queue ----------------------------------------------------------------

    private const string QueueDetailColumns = """
        q.notification_id, q.template_id, t.name AS template_name, q.channel, q.recipient,
        q.subject, q.body, q.is_html, q.priority, q.status, q.retry_count, q.max_retries,
        q.scheduled_at, q.sent_at, q.failed_at, q.last_error, q.source_type, q.source_id, q.created_at
        """;

    public async Task<NotificationQueuePage> ListQueueAsync(
        Guid tenantId, string? status, string? channel, int limit, int offset,
        CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);

        int total;
        await using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandText = """
                SELECT COUNT(*)::int
                FROM notifications.queue q
                WHERE q.tenant_id = @t
                  AND (@status::text IS NULL OR q.status = @status)
                  AND (@channel::text IS NULL OR q.channel = @channel)
                """;
            AddQueueFilterParams(countCmd, tenantId, status, channel);
            total = (int)(await countCmd.ExecuteScalarAsync(cancellationToken))!;
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT q.notification_id, q.template_id, t.name AS template_name, q.channel, q.recipient,
                   q.subject, q.priority, q.status, q.retry_count, q.max_retries,
                   q.scheduled_at, q.sent_at, q.failed_at, q.last_error, q.source_type, q.source_id, q.created_at
            FROM notifications.queue q
            LEFT JOIN notifications.templates t ON t.template_id = q.template_id
            WHERE q.tenant_id = @t
              AND (@status::text IS NULL OR q.status = @status)
              AND (@channel::text IS NULL OR q.channel = @channel)
            ORDER BY q.created_at DESC, q.notification_id DESC
            LIMIT @lim OFFSET @off
            """;
        AddQueueFilterParams(cmd, tenantId, status, channel);
        cmd.Parameters.AddWithValue("lim", limit);
        cmd.Parameters.AddWithValue("off", offset);

        var items = new List<NotificationQueueItem>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new NotificationQueueItem(
                NotificationId: reader.GetInt64(0),
                TemplateId: reader.IsDBNull(1) ? null : reader.GetGuid(1),
                TemplateName: reader.IsDBNull(2) ? null : reader.GetString(2),
                Channel: reader.GetString(3),
                Recipient: reader.GetString(4),
                Subject: reader.IsDBNull(5) ? null : reader.GetString(5),
                Priority: reader.GetInt16(6),
                Status: reader.GetString(7),
                RetryCount: reader.GetInt32(8),
                MaxRetries: reader.GetInt32(9),
                ScheduledAt: new DateTimeOffset(reader.GetDateTime(10), TimeSpan.Zero),
                SentAt: reader.IsDBNull(11) ? null : new DateTimeOffset(reader.GetDateTime(11), TimeSpan.Zero),
                FailedAt: reader.IsDBNull(12) ? null : new DateTimeOffset(reader.GetDateTime(12), TimeSpan.Zero),
                LastError: reader.IsDBNull(13) ? null : reader.GetString(13),
                SourceType: reader.IsDBNull(14) ? null : reader.GetString(14),
                SourceId: reader.IsDBNull(15) ? null : reader.GetString(15),
                CreatedAt: new DateTimeOffset(reader.GetDateTime(16), TimeSpan.Zero)));
        }

        return new NotificationQueuePage(items, total);
    }

    private static void AddQueueFilterParams(NpgsqlCommand cmd, Guid tenantId, string? status, string? channel)
    {
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = (object?)status ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("channel", NpgsqlDbType.Text) { Value = (object?)channel ?? DBNull.Value });
    }

    public async Task<NotificationQueueDetail?> GetQueueItemAsync(Guid tenantId, long notificationId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        return await GetQueueItemCoreAsync(conn, tenantId, notificationId, cancellationToken);
    }

    public async Task<NotificationQueueAction?> CancelAsync(Guid tenantId, long notificationId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        // Only a pending row can be cancelled — 'sending' is already in a sender's hands.
        cmd.CommandText = """
            UPDATE notifications.queue
            SET status = 'cancelled'
            WHERE tenant_id = @t AND notification_id = @id AND status = 'pending'
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("id", notificationId);
        var changed = await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;

        var item = await GetQueueItemCoreAsync(conn, tenantId, notificationId, cancellationToken);
        return item is null ? null : new NotificationQueueAction(changed, item);
    }

    public async Task<NotificationQueueAction?> RetryAsync(Guid tenantId, long notificationId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        // Fresh attempt: retries reset, scheduled now. last_error is kept for context
        // until the next attempt overwrites it.
        cmd.CommandText = """
            UPDATE notifications.queue
            SET status = 'pending', retry_count = 0, scheduled_at = NOW(), failed_at = NULL
            WHERE tenant_id = @t AND notification_id = @id AND status IN ('failed', 'cancelled')
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("id", notificationId);
        var changed = await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;

        var item = await GetQueueItemCoreAsync(conn, tenantId, notificationId, cancellationToken);
        return item is null ? null : new NotificationQueueAction(changed, item);
    }

    private static async Task<NotificationQueueDetail?> GetQueueItemCoreAsync(
        NpgsqlConnection conn, Guid tenantId, long notificationId, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {QueueDetailColumns}
            FROM notifications.queue q
            LEFT JOIN notifications.templates t ON t.template_id = q.template_id
            WHERE q.tenant_id = @t AND q.notification_id = @id
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("id", notificationId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new NotificationQueueDetail(
            NotificationId: reader.GetInt64(0),
            TemplateId: reader.IsDBNull(1) ? null : reader.GetGuid(1),
            TemplateName: reader.IsDBNull(2) ? null : reader.GetString(2),
            Channel: reader.GetString(3),
            Recipient: reader.GetString(4),
            Subject: reader.IsDBNull(5) ? null : reader.GetString(5),
            Body: reader.GetString(6),
            IsHtml: reader.GetBoolean(7),
            Priority: reader.GetInt16(8),
            Status: reader.GetString(9),
            RetryCount: reader.GetInt32(10),
            MaxRetries: reader.GetInt32(11),
            ScheduledAt: new DateTimeOffset(reader.GetDateTime(12), TimeSpan.Zero),
            SentAt: reader.IsDBNull(13) ? null : new DateTimeOffset(reader.GetDateTime(13), TimeSpan.Zero),
            FailedAt: reader.IsDBNull(14) ? null : new DateTimeOffset(reader.GetDateTime(14), TimeSpan.Zero),
            LastError: reader.IsDBNull(15) ? null : reader.GetString(15),
            SourceType: reader.IsDBNull(16) ? null : reader.GetString(16),
            SourceId: reader.IsDBNull(17) ? null : reader.GetString(17),
            CreatedAt: new DateTimeOffset(reader.GetDateTime(18), TimeSpan.Zero));
    }

    // -- Stats ------------------------------------------------------------------

    public async Task<NotificationStats> GetStatsAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);

        int pending, sending, sent24h, failed;
        DateTimeOffset? oldestPending;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT COUNT(*) FILTER (WHERE status = 'pending')::int,
                       COUNT(*) FILTER (WHERE status = 'sending')::int,
                       COUNT(*) FILTER (WHERE status = 'sent' AND sent_at >= NOW() - INTERVAL '24 hours')::int,
                       COUNT(*) FILTER (WHERE status = 'failed')::int,
                       MIN(scheduled_at) FILTER (WHERE status = 'pending')
                FROM notifications.queue
                WHERE tenant_id = @t
                """;
            cmd.Parameters.AddWithValue("t", tenantId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            pending = reader.GetInt32(0);
            sending = reader.GetInt32(1);
            sent24h = reader.GetInt32(2);
            failed = reader.GetInt32(3);
            oldestPending = reader.IsDBNull(4) ? null : new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero);
        }

        var byChannel = new List<NotificationChannelCount>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT channel, COUNT(*)::int
                FROM notifications.queue
                WHERE tenant_id = @t AND created_at >= NOW() - INTERVAL '24 hours'
                GROUP BY channel
                ORDER BY channel
                """;
            cmd.Parameters.AddWithValue("t", tenantId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                byChannel.Add(new NotificationChannelCount(reader.GetString(0), reader.GetInt32(1)));
        }

        return new NotificationStats(pending, sending, sent24h, failed, oldestPending, byChannel);
    }

    // -- Templates ----------------------------------------------------------------

    public async Task<IReadOnlyList<NotificationTemplateSummary>> ListTemplatesAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT template_id, name, channel, is_html, is_active, created_at
            FROM notifications.templates
            WHERE tenant_id = @t
            ORDER BY name
            """;
        cmd.Parameters.AddWithValue("t", tenantId);

        var list = new List<NotificationTemplateSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new NotificationTemplateSummary(
                TemplateId: reader.GetGuid(0),
                Name: reader.GetString(1),
                Channel: reader.GetString(2),
                IsHtml: reader.GetBoolean(3),
                IsActive: reader.GetBoolean(4),
                CreatedAt: new DateTimeOffset(reader.GetDateTime(5), TimeSpan.Zero)));
        }
        return list;
    }

    public async Task<NotificationTemplateDetail?> GetTemplateAsync(Guid tenantId, Guid templateId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {TemplateDetailColumns}
            FROM notifications.templates
            WHERE tenant_id = @t AND template_id = @id
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("id", templateId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTemplateDetail(reader) : null;
    }

    public async Task<NotificationTemplateDetail> CreateTemplateAsync(Guid tenantId, NotificationTemplateSave input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO notifications.templates
                (tenant_id, name, channel, subject_template, body_template, is_html, is_active)
            VALUES (@t, @n, @ch, @subj, @body, @html, @active)
            RETURNING {TemplateDetailColumns}
            """;
        AddTemplateParams(cmd, tenantId, input);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadTemplateDetail(reader);
    }

    public async Task<NotificationTemplateDetail> UpdateTemplateAsync(Guid tenantId, Guid templateId, NotificationTemplateSave input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE notifications.templates
            SET name = @n, channel = @ch, subject_template = @subj, body_template = @body,
                is_html = @html, is_active = @active
            WHERE tenant_id = @t AND template_id = @id
            RETURNING {TemplateDetailColumns}
            """;
        AddTemplateParams(cmd, tenantId, input);
        cmd.Parameters.AddWithValue("id", templateId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new KeyNotFoundException($"Template {templateId} not found for tenant {tenantId}.");
        return ReadTemplateDetail(reader);
    }

    public async Task<NotificationTemplateDetail> SetTemplateActiveAsync(Guid tenantId, Guid templateId, bool isActive, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE notifications.templates
            SET is_active = @active
            WHERE tenant_id = @t AND template_id = @id
            RETURNING {TemplateDetailColumns}
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("id", templateId);
        cmd.Parameters.AddWithValue("active", isActive);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new KeyNotFoundException($"Template {templateId} not found for tenant {tenantId}.");
        return ReadTemplateDetail(reader);
    }

    public async Task<bool> DeleteTemplateAsync(Guid tenantId, Guid templateId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenUnscopedAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM notifications.templates WHERE tenant_id = @t AND template_id = @id";
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("id", templateId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private const string TemplateDetailColumns =
        "template_id, name, channel, subject_template, body_template, is_html, is_active, created_at";

    private static void AddTemplateParams(NpgsqlCommand cmd, Guid tenantId, NotificationTemplateSave input)
    {
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("n", input.Name);
        cmd.Parameters.AddWithValue("ch", input.Channel);
        cmd.Parameters.Add(new NpgsqlParameter("subj", NpgsqlDbType.Text) { Value = (object?)input.SubjectTemplate ?? DBNull.Value });
        cmd.Parameters.AddWithValue("body", input.BodyTemplate);
        cmd.Parameters.AddWithValue("html", input.IsHtml);
        cmd.Parameters.AddWithValue("active", input.IsActive);
    }

    private static NotificationTemplateDetail ReadTemplateDetail(NpgsqlDataReader reader) => new(
        TemplateId: reader.GetGuid(0),
        Name: reader.GetString(1),
        Channel: reader.GetString(2),
        SubjectTemplate: reader.IsDBNull(3) ? null : reader.GetString(3),
        BodyTemplate: reader.GetString(4),
        IsHtml: reader.GetBoolean(5),
        IsActive: reader.GetBoolean(6),
        CreatedAt: new DateTimeOffset(reader.GetDateTime(7), TimeSpan.Zero));
}
