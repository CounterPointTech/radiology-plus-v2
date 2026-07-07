using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using RadiologyPlus.Common.Encryption;
using RadiologyPlus.Core.Audit;
using RadiologyPlus.Core.Billing;
using RadiologyPlus.Core.Data;

namespace RadiologyPlus.Data.Billing;

/// <summary>
/// Real <see cref="IRvuWriteBackSink"/>: pushes our effective work RVUs into the M*Modal
/// ClinicalDataStore (SQL Server). It self-gates on a per-tenant
/// <c>tenancy.mmodal_connections</c> row — absent = not configured = writes nothing — so
/// nothing touches a live DB until a connection is provisioned. The write is diff-only
/// (only codes whose RVU changed), transactional (rolls back on any failure), and
/// dual-audited into <c>audit.access_logs</c> (action 10 = MModalWrite). The match is the
/// active row(s) for a code: <c>WHERE [Code]=@code AND [IsDeleted] IS NULL</c> (optionally
/// scoped to one issuer) — <c>[Code]</c> alone is not unique, the key is
/// <c>(Code, IssuerKey, IsDeleted)</c>.
/// </summary>
public sealed class MModalRvuWriteBackSink : IRvuWriteBackSink
{
    private readonly IAppDbContext _appDb;
    private readonly IEncryptionService _encryption;
    private readonly ILogger<MModalRvuWriteBackSink> _logger;

    // RelativeValueUnit is SQL Server float (double). Compare to our decimal within a small
    // tolerance so float round-trip noise doesn't show every code as "changed".
    private const double RvuEpsilon = 0.00005;

    // Cap the per-change audit detail so a full-table sync doesn't write a giant JSON blob.
    private const int MaxAuditChanges = 500;

    public MModalRvuWriteBackSink(
        IAppDbContext appDb,
        IEncryptionService encryption,
        ILogger<MModalRvuWriteBackSink> logger)
    {
        _appDb = appDb;
        _encryption = encryption;
        _logger = logger;
    }

    public async Task<bool> IsConfiguredAsync(Guid tenantId, CancellationToken cancellationToken = default)
        => await LoadConnectionAsync(tenantId, cancellationToken) is not null;

    public async Task<RvuSyncPreview> PreviewAsync(
        Guid tenantId, short year, char quarter, Guid? issuerKey,
        IReadOnlyList<RvuWriteBackEntry> desired, CancellationToken cancellationToken = default)
    {
        var conn = await LoadConnectionAsync(tenantId, cancellationToken);
        if (conn is null)
            return new RvuSyncPreview(false, year, quarter, 0, 0, 0, 0, Array.Empty<RvuSyncDiff>());

        await using var sql = new SqlConnection(conn.ConnectionString);
        await sql.OpenAsync(cancellationToken);
        var diffs = await ComputeDiffsAsync(sql, issuerKey, desired, cancellationToken);

        return new RvuSyncPreview(
            Configured: true, year, quarter,
            Total: diffs.Count,
            Updatable: diffs.Count(d => d.Action == "update"),
            Unchanged: diffs.Count(d => d.Action == "unchanged"),
            Missing: diffs.Count(d => d.Action == "missing"),
            Diffs: diffs);
    }

    public async Task<RvuSyncResult> ApplyAsync(
        Guid tenantId, short year, char quarter, Guid? issuerKey,
        IReadOnlyList<RvuWriteBackEntry> desired, Guid userId, string username,
        CancellationToken cancellationToken = default)
    {
        var conn = await LoadConnectionAsync(tenantId, cancellationToken);
        if (conn is null)
            return new RvuSyncResult(false, year, quarter, 0, 0, 0, 0, false,
                "M*Modal write-back is not configured for this tenant.", DateTimeOffset.Now);

        await using var sql = new SqlConnection(conn.ConnectionString);
        await sql.OpenAsync(cancellationToken);

        var diffs = await ComputeDiffsAsync(sql, issuerKey, desired, cancellationToken);
        var toUpdate = diffs.Where(d => d.Action == "update").ToList();
        int matched = diffs.Count(d => d.Action != "missing");
        int unchanged = diffs.Count(d => d.Action == "unchanged");
        int missing = diffs.Count(d => d.Action == "missing");

        int updatedCodes = 0;
        if (toUpdate.Count > 0)
        {
            await using var tx = (SqlTransaction)await sql.BeginTransactionAsync(cancellationToken);
            try
            {
                // Scope the write to one issuer (targeted) or, when issuerKey is null, every
                // active row for the code across ALL issuers (the warned power option).
                var updateSql = issuerKey is null
                    ? "UPDATE [Exam].[ExamCode] SET [RelativeValueUnit] = @work, [ModifiedDateTime] = SYSUTCDATETIME() WHERE [Code] = @code AND [IsDeleted] IS NULL"
                    : "UPDATE [Exam].[ExamCode] SET [RelativeValueUnit] = @work, [ModifiedDateTime] = SYSUTCDATETIME() WHERE [Code] = @code AND [IsDeleted] IS NULL AND [IssuerKey] = @issuer";

                foreach (var d in toUpdate)
                {
                    await using var cmd = sql.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = updateSql;
                    cmd.CommandTimeout = 120;
                    cmd.Parameters.Add(new SqlParameter("@work", SqlDbType.Float) { Value = (double)d.NewRvu });
                    cmd.Parameters.Add(new SqlParameter("@code", SqlDbType.NVarChar, 50) { Value = d.Hcpcs });
                    if (issuerKey is Guid ik)
                        cmd.Parameters.Add(new SqlParameter("@issuer", SqlDbType.UniqueIdentifier) { Value = ik });

                    var rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
                    if (rows > 0) updatedCodes++;
                }

                await tx.CommitAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(cancellationToken);
                _logger.LogError(ex,
                    "M*Modal RVU write-back failed for tenant {Tenant} {Year}{Quarter} (issuer {Issuer}); rolled back.",
                    tenantId, year, quarter, issuerKey?.ToString() ?? "ALL");
                await WriteAuditAsync(tenantId, userId, username, year, quarter, issuerKey,
                    success: false, matched, updated: 0, unchanged, missing, toUpdate, ex.Message, cancellationToken);
                return new RvuSyncResult(true, year, quarter, matched, 0, unchanged, missing, false, ex.Message, DateTimeOffset.Now);
            }
        }

        await WriteAuditAsync(tenantId, userId, username, year, quarter, issuerKey,
            success: true, matched, updatedCodes, unchanged, missing, toUpdate, null, cancellationToken);
        return new RvuSyncResult(true, year, quarter, matched, updatedCodes, unchanged, missing, true, null, DateTimeOffset.Now);
    }

    public async Task<IReadOnlyList<MModalIssuer>> ListIssuersAsync(
        Guid tenantId, CancellationToken cancellationToken = default)
    {
        var conn = await LoadConnectionAsync(tenantId, cancellationToken);
        if (conn is null) return Array.Empty<MModalIssuer>();

        await using var sql = new SqlConnection(conn.ConnectionString);
        await sql.OpenAsync(cancellationToken);
        await using var cmd = sql.CreateCommand();
        cmd.CommandTimeout = 120;
        // Only issuers with at least one active exam code — the rest can't be a sync target.
        cmd.CommandText = """
            SELECT i.[IssuerKey], i.[Name], i.[Description], COUNT(ec.[ExamCodeId]) AS active_codes
            FROM [Clinical].[Issuer] i
            JOIN [Exam].[ExamCode] ec
              ON ec.[IssuerKey] = i.[IssuerKey] AND ec.[IsDeleted] IS NULL
            GROUP BY i.[IssuerKey], i.[Name], i.[Description]
            HAVING COUNT(ec.[ExamCodeId]) > 0
            ORDER BY COUNT(ec.[ExamCodeId]) DESC
            """;

        var result = new List<MModalIssuer>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = reader.GetGuid(0);
            result.Add(new MModalIssuer(
                IssuerKey: key,
                Name: reader.IsDBNull(1) ? key.ToString() : reader.GetString(1),
                Description: reader.IsDBNull(2) ? null : reader.GetString(2),
                ActiveCodeCount: reader.GetInt32(3),
                IsDefault: conn.IssuerKey is Guid def && def == key));
        }
        return result;
    }

    // ── Diff: read the active M*Modal rows once, aggregate per Code, classify each desired
    //    entry as update / unchanged / missing. A code is "unchanged" only when every active
    //    row already equals the new RVU (no NULLs, uniform value); otherwise "update".
    private static async Task<List<RvuSyncDiff>> ComputeDiffsAsync(
        SqlConnection sql, Guid? issuerKey,
        IReadOnlyList<RvuWriteBackEntry> desired, CancellationToken ct)
    {
        var current = new Dictionary<string, CodeAggregate>(StringComparer.OrdinalIgnoreCase);

        await using (var cmd = sql.CreateCommand())
        {
            cmd.CommandTimeout = 120;
            cmd.CommandText = issuerKey is null
                ? """
                  SELECT [Code], COUNT(*) AS n,
                         SUM(CASE WHEN [RelativeValueUnit] IS NULL THEN 1 ELSE 0 END) AS n_null,
                         MIN([RelativeValueUnit]) AS min_rvu, MAX([RelativeValueUnit]) AS max_rvu
                  FROM [Exam].[ExamCode]
                  WHERE [IsDeleted] IS NULL
                  GROUP BY [Code]
                  """
                : """
                  SELECT [Code], COUNT(*) AS n,
                         SUM(CASE WHEN [RelativeValueUnit] IS NULL THEN 1 ELSE 0 END) AS n_null,
                         MIN([RelativeValueUnit]) AS min_rvu, MAX([RelativeValueUnit]) AS max_rvu
                  FROM [Exam].[ExamCode]
                  WHERE [IsDeleted] IS NULL AND [IssuerKey] = @issuer
                  GROUP BY [Code]
                  """;
            if (issuerKey is Guid ik)
                cmd.Parameters.Add(new SqlParameter("@issuer", SqlDbType.UniqueIdentifier) { Value = ik });

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var code = reader.GetString(0);
                var n = reader.GetInt32(1);
                var nNull = reader.GetInt32(2);
                double? min = reader.IsDBNull(3) ? null : reader.GetDouble(3);
                double? max = reader.IsDBNull(4) ? null : reader.GetDouble(4);
                current[code] = new CodeAggregate(n, nNull, min, max);
            }
        }

        var diffs = new List<RvuSyncDiff>(desired.Count);
        foreach (var e in desired)
        {
            if (!current.TryGetValue(e.Hcpcs, out var agg) || agg.RowCount == 0)
            {
                diffs.Add(new RvuSyncDiff(e.Hcpcs, CurrentRvu: null, NewRvu: e.WorkRvu, MatchedRows: 0, Action: "missing"));
                continue;
            }

            var newD = (double)e.WorkRvu;
            var uniform = agg.NullCount == 0 && agg.Min.HasValue && agg.Max.HasValue
                          && Math.Abs(agg.Min.Value - agg.Max.Value) <= RvuEpsilon;
            var unchanged = uniform && Math.Abs(agg.Min!.Value - newD) <= RvuEpsilon;
            decimal? currentRvu = uniform ? (decimal)agg.Min!.Value : null;   // mixed/NULL -> show blank, will update

            diffs.Add(new RvuSyncDiff(
                e.Hcpcs,
                CurrentRvu: currentRvu,
                NewRvu: e.WorkRvu,
                MatchedRows: agg.RowCount,
                Action: unchanged ? "unchanged" : "update"));
        }

        return diffs;
    }

    private readonly record struct CodeAggregate(int RowCount, int NullCount, double? Min, double? Max);

    // ── Backup: read the current RVU of every active exam code in scope (blanks -> null).
    public async Task<IReadOnlyList<RvuSnapshotRow>> CaptureCurrentRvusAsync(
        Guid tenantId, Guid? issuerKey, CancellationToken cancellationToken = default)
    {
        var conn = await LoadConnectionAsync(tenantId, cancellationToken);
        if (conn is null) return Array.Empty<RvuSnapshotRow>();

        await using var sql = new SqlConnection(conn.ConnectionString);
        await sql.OpenAsync(cancellationToken);
        await using var cmd = sql.CreateCommand();
        cmd.CommandTimeout = 120;
        cmd.CommandText = issuerKey is null
            ? "SELECT [IssuerKey], [Code], [RelativeValueUnit] FROM [Exam].[ExamCode] WHERE [IsDeleted] IS NULL"
            : "SELECT [IssuerKey], [Code], [RelativeValueUnit] FROM [Exam].[ExamCode] WHERE [IsDeleted] IS NULL AND [IssuerKey] = @issuer";
        if (issuerKey is Guid ik)
            cmd.Parameters.Add(new SqlParameter("@issuer", SqlDbType.UniqueIdentifier) { Value = ik });

        var rows = new List<RvuSnapshotRow>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new RvuSnapshotRow(
                IssuerKey: reader.GetGuid(0),
                Code: reader.GetString(1),
                Rvu: reader.IsDBNull(2) ? null : reader.GetDouble(2)));
        }
        return rows;
    }

    // ── Restore: write snapshot values back (diff-only, per-row issuer-scoped, blanks verbatim).
    public async Task<RvuRestoreResult> RestoreRvusAsync(
        Guid tenantId, Guid? issuerKey, IReadOnlyList<RvuSnapshotRow> rows,
        Guid userId, string username, CancellationToken cancellationToken = default)
    {
        var conn = await LoadConnectionAsync(tenantId, cancellationToken);
        if (conn is null)
            return new RvuRestoreResult(false, 0, 0, 0, false,
                "M*Modal write-back is not configured for this tenant.", DateTimeOffset.Now);

        await using var sql = new SqlConnection(conn.ConnectionString);
        await sql.OpenAsync(cancellationToken);

        // Present state for the scope, keyed (issuer, code). Presence = an active row exists.
        var current = new Dictionary<(Guid, string), double?>();
        await using (var read = sql.CreateCommand())
        {
            read.CommandTimeout = 120;
            read.CommandText = issuerKey is null
                ? "SELECT [IssuerKey], [Code], [RelativeValueUnit] FROM [Exam].[ExamCode] WHERE [IsDeleted] IS NULL"
                : "SELECT [IssuerKey], [Code], [RelativeValueUnit] FROM [Exam].[ExamCode] WHERE [IsDeleted] IS NULL AND [IssuerKey] = @issuer";
            if (issuerKey is Guid ik0)
                read.Parameters.Add(new SqlParameter("@issuer", SqlDbType.UniqueIdentifier) { Value = ik0 });
            await using var rdr = await read.ExecuteReaderAsync(cancellationToken);
            while (await rdr.ReadAsync(cancellationToken))
                current[(rdr.GetGuid(0), rdr.GetString(1))] = rdr.IsDBNull(2) ? null : rdr.GetDouble(2);
        }

        var toRestore = new List<RvuSnapshotRow>();
        int unchanged = 0, missing = 0;
        foreach (var r in rows)
        {
            // TryGetValue is false only when no active row exists (a null RVU is a present key).
            if (!current.TryGetValue((r.IssuerKey, r.Code), out var cur)) { missing++; continue; }
            var same = (cur is null && r.Rvu is null)
                || (cur.HasValue && r.Rvu.HasValue && Math.Abs(cur.Value - r.Rvu.Value) <= RvuEpsilon);
            if (same) { unchanged++; continue; }
            toRestore.Add(r);
        }

        int restored = 0;
        if (toRestore.Count > 0)
        {
            await using var tx = (SqlTransaction)await sql.BeginTransactionAsync(cancellationToken);
            try
            {
                const string updateSql =
                    "UPDATE [Exam].[ExamCode] SET [RelativeValueUnit] = @rvu, [ModifiedDateTime] = SYSUTCDATETIME() " +
                    "WHERE [Code] = @code AND [IsDeleted] IS NULL AND [IssuerKey] = @issuer";
                foreach (var r in toRestore)
                {
                    await using var cmd = sql.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = updateSql;
                    cmd.CommandTimeout = 120;
                    cmd.Parameters.Add(new SqlParameter("@rvu", SqlDbType.Float) { Value = (object?)r.Rvu ?? DBNull.Value });
                    cmd.Parameters.Add(new SqlParameter("@code", SqlDbType.NVarChar, 50) { Value = r.Code });
                    cmd.Parameters.Add(new SqlParameter("@issuer", SqlDbType.UniqueIdentifier) { Value = r.IssuerKey });
                    if (await cmd.ExecuteNonQueryAsync(cancellationToken) > 0) restored++;
                }
                await tx.CommitAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(cancellationToken);
                _logger.LogError(ex, "M*Modal RVU restore failed for tenant {Tenant}; rolled back.", tenantId);
                await InsertAuditAsync(tenantId, userId, username, "billing.rvu_restore", issuerKey?.ToString() ?? "ALL",
                    success: false, ex.Message, RestoreMetadata(issuerKey, 0, unchanged, missing, false), cancellationToken);
                return new RvuRestoreResult(true, 0, unchanged, missing, false, ex.Message, DateTimeOffset.Now);
            }
        }

        await InsertAuditAsync(tenantId, userId, username, "billing.rvu_restore", issuerKey?.ToString() ?? "ALL",
            success: true, null, RestoreMetadata(issuerKey, restored, unchanged, missing, true), cancellationToken);
        return new RvuRestoreResult(true, restored, unchanged, missing, true, null, DateTimeOffset.Now);
    }

    private static object RestoreMetadata(Guid? issuerKey, int restored, int unchanged, int missing, bool success) => new
    {
        description = success
            ? $"M*Modal RVU restore (issuer {issuerKey?.ToString() ?? "ALL"}): {restored} restored, {unchanged} unchanged, {missing} missing."
            : $"M*Modal RVU restore (issuer {issuerKey?.ToString() ?? "ALL"}) FAILED and was rolled back.",
        issuerKey,
        restored,
        unchanged,
        missing,
    };

    // ── Dual audit: write our append-only audit.access_logs row (the M*Modal write itself
    //    already committed/rolled back). before/after live in metadata.changes.
    private Task WriteAuditAsync(
        Guid tenantId, Guid userId, string username, short year, char quarter, Guid? issuerKey,
        bool success, int matched, int updated, int unchanged, int missing,
        List<RvuSyncDiff> changes, string? error, CancellationToken ct)
    {
        var scope = issuerKey?.ToString() ?? "ALL issuers";
        var metadata = new
        {
            description = success
                ? $"M*Modal RVU write-back {year}{quarter} (issuer {scope}): {updated} updated, {unchanged} unchanged, {missing} missing."
                : $"M*Modal RVU write-back {year}{quarter} (issuer {scope}) FAILED and was rolled back.",
            year,
            quarter = quarter.ToString(),
            issuerKey,
            matched,
            updated,
            unchanged,
            missing,
            changes = changes.Take(MaxAuditChanges)
                .Select(c => new { hcpcs = c.Hcpcs, from = c.CurrentRvu, to = c.NewRvu })
                .ToArray(),
            changesTruncated = changes.Count > MaxAuditChanges,
        };
        return InsertAuditAsync(tenantId, userId, username, "billing.rvu_writeback", $"{year}{quarter}", success, error, metadata, ct);
    }

    private async Task InsertAuditAsync(
        Guid tenantId, Guid userId, string username, string resourceType, string resourceId,
        bool success, string? error, object metadata, CancellationToken ct)
    {
        try
        {
            await using var conn = (NpgsqlConnection)await _appDb.OpenUnscopedAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO audit.access_logs
                    (tenant_id, user_id, username, action, resource_type, resource_id, success,
                     error_message, metadata, occurred_at)
                VALUES (@tenant, @user, @username, @action, @rtype, @rid, @success,
                        @error, @meta::jsonb, NOW())
                """;
            cmd.Parameters.AddWithValue("tenant", tenantId);
            cmd.Parameters.Add(new NpgsqlParameter("user", NpgsqlDbType.Uuid) { Value = userId });
            cmd.Parameters.Add(new NpgsqlParameter("username", NpgsqlDbType.Text) { Value = (object?)username ?? DBNull.Value });
            cmd.Parameters.AddWithValue("action", (short)AccessAction.MModalWrite);
            cmd.Parameters.AddWithValue("rtype", resourceType);
            cmd.Parameters.AddWithValue("rid", resourceId);
            cmd.Parameters.AddWithValue("success", success);
            cmd.Parameters.Add(new NpgsqlParameter("error", NpgsqlDbType.Text) { Value = (object?)error ?? DBNull.Value });
            cmd.Parameters.AddWithValue("meta", JsonSerializer.Serialize(metadata));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            // Audit-after-commit: never let an audit-write failure mask the actual write outcome.
            _logger.LogError(ex,
                "M*Modal write-back: failed to write audit.access_logs for tenant {Tenant} ({Resource}).",
                tenantId, resourceType);
        }
    }

    // ── Resolve + decrypt the per-tenant M*Modal connection. Null when none configured.
    //    Mirrors NovaradConnectionPool.BuildDataSourceAsync (unscoped read + explicit filter).
    private async Task<MModalConnection?> LoadConnectionAsync(Guid tenantId, CancellationToken ct)
    {
        await using var conn = (NpgsqlConnection)await _appDb.OpenUnscopedAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT host, port, database_name, username, password_encrypted, use_ssl,
                   trust_server_cert, issuer_key
            FROM tenancy.mmodal_connections
            WHERE tenant_id = @t
            """;
        cmd.Parameters.AddWithValue("t", tenantId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var host = reader.GetString(0);
        var port = reader.GetInt32(1);
        var database = reader.GetString(2);
        var username = reader.GetString(3);
        var passwordBytes = (byte[])reader.GetValue(4);
        var useSsl = reader.GetBoolean(5);
        var trustCert = reader.GetBoolean(6);
        Guid? issuerKey = reader.IsDBNull(7) ? null : reader.GetGuid(7);
        var password = _encryption.Decrypt(passwordBytes);

        var b = new SqlConnectionStringBuilder
        {
            DataSource = port == 1433 ? host : $"{host},{port}",
            InitialCatalog = database,
            UserID = username,
            Password = password,
            ApplicationName = "RadiologyPlus",
            Encrypt = useSsl,
            TrustServerCertificate = trustCert,
            ConnectTimeout = 15,
        };

        return new MModalConnection(b.ConnectionString, issuerKey);
    }

    private sealed record MModalConnection(string ConnectionString, Guid? IssuerKey);
}
