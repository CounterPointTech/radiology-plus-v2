using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using RadiologyPlus.Core.Billing;
using RadiologyPlus.Core.Data;

namespace RadiologyPlus.Data.Billing;

public sealed class BillingRepository : IBillingRepository
{
    private readonly IAppDbContext _db;

    public BillingRepository(IAppDbContext db) => _db = db;

    public async Task<CptImport> ImportCptMasterAsync(
        Guid tenantId,
        Guid runByUserId,
        string fileName,
        string sheetName,
        short year,
        IReadOnlyList<CptCodeUpsert> rows,
        IReadOnlyList<CptImportError> parseErrors,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(parseErrors);

        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        // 1. Insert the import header so we have an import_id to tag rows with.
        long importId;
        await using (var insertHeader = conn.CreateCommand())
        {
            insertHeader.Transaction = tx;
            insertHeader.CommandText = """
                INSERT INTO billing.cpt_imports
                    (tenant_id, file_name, sheet_name, year, parsed_rows, skipped_rows, errors, ran_by_user_id)
                VALUES (@t, @file, @sheet, @year, @parsed, @skipped, @errors::jsonb, @user)
                RETURNING import_id
                """;
            insertHeader.Parameters.AddWithValue("t", tenantId);
            insertHeader.Parameters.AddWithValue("file", fileName);
            insertHeader.Parameters.AddWithValue("sheet", sheetName);
            insertHeader.Parameters.AddWithValue("year", year);
            insertHeader.Parameters.AddWithValue("parsed", rows.Count);
            insertHeader.Parameters.AddWithValue("skipped", parseErrors.Count);
            insertHeader.Parameters.AddWithValue("errors", JsonSerializer.Serialize(parseErrors));
            insertHeader.Parameters.AddWithValue("user", runByUserId);
            var result = await insertHeader.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("INSERT cpt_imports did not return an id.");
            importId = (long)result;
        }

        // 2. Bulk upsert. Use the unnest pattern so we send one round-trip regardless of row count.
        int inserted = 0, updated = 0;
        if (rows.Count > 0)
        {
            var codes = new string[rows.Count];
            var descriptions = new string[rows.Count];
            var rvus = new decimal[rows.Count];
            for (int i = 0; i < rows.Count; i++)
            {
                codes[i] = rows[i].Code;
                descriptions[i] = rows[i].Description;
                rvus[i] = rows[i].WorkRvu;
            }

            await using var upsert = conn.CreateCommand();
            upsert.Transaction = tx;
            upsert.CommandText = """
                WITH new_rows AS (
                    SELECT UNNEST(@codes) AS cpt_code,
                           UNNEST(@descs) AS description,
                           UNNEST(@rvus)::numeric(8,4) AS work_rvu
                ),
                ins AS (
                    INSERT INTO billing.cpt_codes
                        (tenant_id, year, cpt_code, description, work_rvu, imported_from_import_id)
                    SELECT @t, @year, cpt_code, description, work_rvu, @import
                    FROM new_rows
                    ON CONFLICT (tenant_id, year, cpt_code) DO UPDATE
                        SET description = EXCLUDED.description,
                            work_rvu    = EXCLUDED.work_rvu,
                            updated_at  = NOW(),
                            imported_from_import_id = EXCLUDED.imported_from_import_id
                    RETURNING (xmax = 0) AS inserted
                )
                SELECT COUNT(*) FILTER (WHERE inserted) AS inserted_count,
                       COUNT(*) FILTER (WHERE NOT inserted) AS updated_count
                FROM ins
                """;
            upsert.Parameters.AddWithValue("t", tenantId);
            upsert.Parameters.AddWithValue("year", year);
            upsert.Parameters.AddWithValue("import", importId);
            upsert.Parameters.Add(new NpgsqlParameter("codes", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = codes });
            upsert.Parameters.Add(new NpgsqlParameter("descs", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = descriptions });
            upsert.Parameters.Add(new NpgsqlParameter("rvus", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = rvus });
            await using var reader = await upsert.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                inserted = checked((int)reader.GetInt64(0));
                updated = checked((int)reader.GetInt64(1));
            }
        }

        // 3. Update header counts.
        await using (var updateHeader = conn.CreateCommand())
        {
            updateHeader.Transaction = tx;
            updateHeader.CommandText = """
                UPDATE billing.cpt_imports
                SET inserted_rows = @ins,
                    updated_rows  = @upd
                WHERE import_id = @id
                """;
            updateHeader.Parameters.AddWithValue("id", importId);
            updateHeader.Parameters.AddWithValue("ins", inserted);
            updateHeader.Parameters.AddWithValue("upd", updated);
            await updateHeader.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);

        return new CptImport(
            ImportId: importId,
            FileName: fileName,
            SheetName: sheetName,
            Year: year,
            ParsedRows: rows.Count,
            InsertedRows: inserted,
            UpdatedRows: updated,
            SkippedRows: parseErrors.Count,
            Errors: parseErrors,
            RanByUserId: runByUserId,
            RanAt: DateTimeOffset.UtcNow);
    }

    public async Task<RvuImport> ImportRvuValuesAsync(
        Guid tenantId,
        Guid runByUserId,
        string fileName,
        short year,
        char quarter,
        IReadOnlyList<RvuValueUpsert> rows,
        IReadOnlyList<CptImportError> parseErrors,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(parseErrors);

        var q = char.ToUpperInvariant(quarter);
        if (q is not ('A' or 'B' or 'C' or 'D'))
            throw new ArgumentOutOfRangeException(nameof(quarter), quarter, "quarter must be A, B, C, or D.");
        var quarterStr = q.ToString();
        var effectiveFrom = EffectiveFromForQuarter(year, q);

        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        // 1. Insert the import header so we have an import_id to tag rows with.
        long importId;
        await using (var insertHeader = conn.CreateCommand())
        {
            insertHeader.Transaction = tx;
            insertHeader.CommandText = """
                INSERT INTO billing.rvu_imports
                    (tenant_id, file_name, year, quarter, parsed_rows, skipped_rows, errors, ran_by_user_id)
                VALUES (@t, @file, @year, @q, @parsed, @skipped, @errors::jsonb, @user)
                RETURNING import_id
                """;
            insertHeader.Parameters.AddWithValue("t", tenantId);
            insertHeader.Parameters.AddWithValue("file", fileName);
            insertHeader.Parameters.AddWithValue("year", year);
            insertHeader.Parameters.AddWithValue("q", quarterStr);
            insertHeader.Parameters.AddWithValue("parsed", rows.Count);
            insertHeader.Parameters.AddWithValue("skipped", parseErrors.Count);
            insertHeader.Parameters.AddWithValue("errors", JsonSerializer.Serialize(parseErrors));
            insertHeader.Parameters.AddWithValue("user", runByUserId);
            var result = await insertHeader.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("INSERT rvu_imports did not return an id.");
            importId = (long)result;
        }

        // 2. Bulk upsert via UNNEST — one round-trip regardless of row count (~19k).
        //    The importer already de-duped (hcpcs, modifier), so ON CONFLICT never
        //    touches the same row twice within this statement.
        int inserted = 0, updated = 0;
        if (rows.Count > 0)
        {
            int n = rows.Count;
            var hcpcs = new string[n];
            var mods = new string[n];
            var descs = new string?[n];
            var work = new decimal[n];
            var peNf = new decimal?[n];
            var peF = new decimal?[n];
            var mp = new decimal?[n];
            var totNf = new decimal?[n];
            var totF = new decimal?[n];
            var status = new string?[n];
            var glob = new string?[n];
            for (int i = 0; i < n; i++)
            {
                var r = rows[i];
                hcpcs[i] = r.Hcpcs; mods[i] = r.Modifier; descs[i] = r.Description;
                work[i] = r.WorkRvu; peNf[i] = r.PeRvuNonFac; peF[i] = r.PeRvuFac;
                mp[i] = r.MpRvu; totNf[i] = r.TotalNonFac; totF[i] = r.TotalFac;
                status[i] = r.StatusCode; glob[i] = r.GlobalDays;
            }

            await using var upsert = conn.CreateCommand();
            upsert.Transaction = tx;
            upsert.CommandText = """
                WITH new_rows AS (
                    SELECT * FROM UNNEST(
                        @hcpcs, @mods, @descs, @work, @peNf, @peF, @mp, @totNf, @totF, @status, @glob
                    ) AS t(hcpcs, modifier, description, work_rvu, pe_nf, pe_f, mp, tot_nf, tot_f, status_code, global_days)
                ),
                ins AS (
                    INSERT INTO billing.rvu_values
                        (tenant_id, year, quarter, hcpcs, modifier, description, work_rvu,
                         pe_rvu_nonfac, pe_rvu_fac, mp_rvu, total_nonfac, total_fac,
                         status_code, global_days, effective_from, source_import_id)
                    SELECT @t, @year, @q, hcpcs, modifier, description, work_rvu::numeric(8,4),
                           pe_nf::numeric(8,4), pe_f::numeric(8,4), mp::numeric(8,4),
                           tot_nf::numeric(8,4), tot_f::numeric(8,4),
                           status_code, global_days, @eff, @import
                    FROM new_rows
                    ON CONFLICT (tenant_id, year, quarter, hcpcs, modifier) DO UPDATE
                        SET description      = EXCLUDED.description,
                            work_rvu         = EXCLUDED.work_rvu,
                            pe_rvu_nonfac    = EXCLUDED.pe_rvu_nonfac,
                            pe_rvu_fac       = EXCLUDED.pe_rvu_fac,
                            mp_rvu           = EXCLUDED.mp_rvu,
                            total_nonfac     = EXCLUDED.total_nonfac,
                            total_fac        = EXCLUDED.total_fac,
                            status_code      = EXCLUDED.status_code,
                            global_days      = EXCLUDED.global_days,
                            effective_from   = EXCLUDED.effective_from,
                            source_import_id = EXCLUDED.source_import_id,
                            updated_at       = NOW()
                    RETURNING (xmax = 0) AS inserted
                )
                SELECT COUNT(*) FILTER (WHERE inserted) AS inserted_count,
                       COUNT(*) FILTER (WHERE NOT inserted) AS updated_count
                FROM ins
                """;
            upsert.Parameters.AddWithValue("t", tenantId);
            upsert.Parameters.AddWithValue("year", year);
            upsert.Parameters.AddWithValue("q", quarterStr);
            upsert.Parameters.Add(new NpgsqlParameter("eff", NpgsqlDbType.Date) { Value = effectiveFrom });
            upsert.Parameters.AddWithValue("import", importId);
            upsert.Parameters.Add(new NpgsqlParameter("hcpcs", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = hcpcs });
            upsert.Parameters.Add(new NpgsqlParameter("mods", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = mods });
            upsert.Parameters.Add(new NpgsqlParameter("descs", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = descs });
            upsert.Parameters.Add(new NpgsqlParameter("work", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = work });
            upsert.Parameters.Add(new NpgsqlParameter("peNf", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = peNf });
            upsert.Parameters.Add(new NpgsqlParameter("peF", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = peF });
            upsert.Parameters.Add(new NpgsqlParameter("mp", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = mp });
            upsert.Parameters.Add(new NpgsqlParameter("totNf", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = totNf });
            upsert.Parameters.Add(new NpgsqlParameter("totF", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = totF });
            upsert.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = status });
            upsert.Parameters.Add(new NpgsqlParameter("glob", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = glob });
            await using var reader = await upsert.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                inserted = checked((int)reader.GetInt64(0));
                updated = checked((int)reader.GetInt64(1));
            }
        }

        // 3. Update header counts.
        await using (var updateHeader = conn.CreateCommand())
        {
            updateHeader.Transaction = tx;
            updateHeader.CommandText = """
                UPDATE billing.rvu_imports SET inserted_rows = @ins, updated_rows = @upd
                WHERE import_id = @id
                """;
            updateHeader.Parameters.AddWithValue("id", importId);
            updateHeader.Parameters.AddWithValue("ins", inserted);
            updateHeader.Parameters.AddWithValue("upd", updated);
            await updateHeader.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);

        return new RvuImport(
            ImportId: importId,
            FileName: fileName,
            Year: year,
            Quarter: q,
            ParsedRows: rows.Count,
            InsertedRows: inserted,
            UpdatedRows: updated,
            SkippedRows: parseErrors.Count,
            Errors: parseErrors,
            RanByUserId: runByUserId,
            RanAt: DateTimeOffset.UtcNow);
    }

    // A=Jan, B=Apr, C=Jul, D=Oct — the quarter a CMS RVU release takes effect.
    private static DateOnly EffectiveFromForQuarter(short year, char quarter) =>
        char.ToUpperInvariant(quarter) switch
        {
            'B' => new DateOnly(year, 4, 1),
            'C' => new DateOnly(year, 7, 1),
            'D' => new DateOnly(year, 10, 1),
            _ => new DateOnly(year, 1, 1),
        };

    public async Task<IReadOnlyList<RvuValue>> ListRvuValuesAsync(
        Guid tenantId,
        short? year,
        char? quarter,
        string? search,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        var hasSearch = !string.IsNullOrWhiteSpace(search);
        cmd.CommandText = $"""
            SELECT year, quarter, hcpcs, modifier, description, work_rvu,
                   pe_rvu_nonfac, pe_rvu_fac, mp_rvu, total_nonfac, total_fac,
                   status_code, global_days, effective_from, source_import_id,
                   created_at, updated_at
            FROM billing.rvu_values
            WHERE tenant_id = @t
              {(year is null ? "" : "AND year = @year")}
              {(quarter is null ? "" : "AND quarter = @q")}
              {(hasSearch ? "AND (description ILIKE @search OR hcpcs ILIKE @search)" : "")}
            ORDER BY hcpcs, modifier
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        if (year is not null) cmd.Parameters.AddWithValue("year", year.Value);
        if (quarter is not null) cmd.Parameters.AddWithValue("q", char.ToUpperInvariant(quarter.Value).ToString());
        if (hasSearch) cmd.Parameters.AddWithValue("search", $"%{search!.Trim()}%");
        cmd.Parameters.AddWithValue("limit", limit);

        var result = new List<RvuValue>();
        await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await rdr.ReadAsync(cancellationToken))
        {
            result.Add(new RvuValue(
                Year:           rdr.GetInt16(0),
                Quarter:        rdr.GetString(1)[0],
                Hcpcs:          rdr.GetString(2),
                Modifier:       rdr.GetString(3),
                Description:    rdr.IsDBNull(4) ? null : rdr.GetString(4),
                WorkRvu:        rdr.GetDecimal(5),
                PeRvuNonFac:    rdr.IsDBNull(6) ? null : rdr.GetDecimal(6),
                PeRvuFac:       rdr.IsDBNull(7) ? null : rdr.GetDecimal(7),
                MpRvu:          rdr.IsDBNull(8) ? null : rdr.GetDecimal(8),
                TotalNonFac:    rdr.IsDBNull(9) ? null : rdr.GetDecimal(9),
                TotalFac:       rdr.IsDBNull(10) ? null : rdr.GetDecimal(10),
                StatusCode:     rdr.IsDBNull(11) ? null : rdr.GetString(11),
                GlobalDays:     rdr.IsDBNull(12) ? null : rdr.GetString(12),
                EffectiveFrom:  rdr.IsDBNull(13) ? null : rdr.GetFieldValue<DateOnly>(13),
                SourceImportId: rdr.IsDBNull(14) ? null : rdr.GetInt64(14),
                CreatedAt:      new DateTimeOffset(rdr.GetDateTime(15), TimeSpan.Zero),
                UpdatedAt:      new DateTimeOffset(rdr.GetDateTime(16), TimeSpan.Zero)));
        }
        return result;
    }

    // ------------------------------------------------------------------------
    // Item 1.2 (mgmt UI) — manual RVU overrides + the CMS-check management view.
    // ------------------------------------------------------------------------

    public async Task<IReadOnlyList<RvuOverride>> ListRvuOverridesAsync(
        Guid tenantId, short? year, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT override_id, year, cpt_code, facility_id, override_work_rvu, note,
                   created_by_user_id, created_at, updated_at
            FROM billing.rvu_overrides
            WHERE tenant_id = @t
              {(year is null ? "" : "AND year = @year")}
              AND facility_id IS NULL
            ORDER BY year DESC, cpt_code
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        if (year is not null) cmd.Parameters.AddWithValue("year", year.Value);

        var result = new List<RvuOverride>();
        await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await rdr.ReadAsync(cancellationToken))
            result.Add(ReadOverride(rdr));
        return result;
    }

    public async Task<RvuOverrideUpsertResult> UpsertRvuOverrideAsync(
        Guid tenantId, Guid userId, RvuOverrideUpsert upsert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upsert);
        var raw = upsert.Code?.Trim() ?? "";
        if (raw.Length == 0)
            throw new ArgumentException("Override code must be non-empty.", nameof(upsert));
        // Canonicalize a bundle to its sorted/deduped set-key (a single to NormalizeCpt) so
        // storage, the unique index, the reconciliation overlay, the cms-check display, and
        // DELETE all agree on one spelling. Otherwise "A;B" and "B;A" insert as distinct rows
        // yet collapse to the same bundle in the credit path — a non-deterministic credit.
        var code = raw.Contains(';')
            ? NormalizeCptSetKey(raw.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            : NormalizeCpt(raw);
        if (upsert.OverrideWorkRvu < 0)
            throw new ArgumentOutOfRangeException(nameof(upsert), upsert.OverrideWorkRvu, "override_work_rvu must be non-negative.");

        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        // Conflict target is the partial unique index uq_rvu_override_tenant_year_cpt_all
        // (tenant_id, year, cpt_code) WHERE facility_id IS NULL. (xmax = 0) distinguishes
        // an insert from an update for the audit action. created_by_user_id is set once.
        cmd.CommandText = """
            INSERT INTO billing.rvu_overrides
                (tenant_id, year, cpt_code, facility_id, override_work_rvu, note, created_by_user_id)
            VALUES (@t, @year, @code, NULL, @work, @note, @user)
            ON CONFLICT (tenant_id, year, cpt_code) WHERE facility_id IS NULL
            DO UPDATE SET override_work_rvu = EXCLUDED.override_work_rvu,
                          -- Preserve an existing note when the caller omits one (the inline
                          -- RVU edit sends no note); only overwrite when a note is supplied.
                          note              = COALESCE(EXCLUDED.note, billing.rvu_overrides.note),
                          updated_at        = NOW()
            RETURNING override_id, year, cpt_code, facility_id, override_work_rvu, note,
                      created_by_user_id, created_at, updated_at, (xmax = 0) AS inserted
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("year", upsert.Year);
        cmd.Parameters.AddWithValue("code", code);
        cmd.Parameters.Add(new NpgsqlParameter("work", NpgsqlDbType.Numeric) { Value = upsert.OverrideWorkRvu });
        cmd.Parameters.AddWithValue("note", (object?)upsert.Note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("user", userId);

        await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await rdr.ReadAsync(cancellationToken))
            throw new InvalidOperationException("rvu_overrides upsert returned no row.");
        var row = ReadOverride(rdr);
        var inserted = rdr.GetBoolean(9);
        return new RvuOverrideUpsertResult(row, inserted);
    }

    public async Task<bool> DeleteRvuOverrideAsync(
        Guid tenantId, short year, string code, CancellationToken cancellationToken = default)
    {
        // Canonicalize the same way Upsert stores it, so a delete targets the stored row even
        // when the caller passes a different bundle component order/case.
        var raw = code?.Trim() ?? "";
        var normalized = raw.Contains(';')
            ? NormalizeCptSetKey(raw.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            : NormalizeCpt(raw);
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM billing.rvu_overrides
            WHERE tenant_id = @t AND year = @year AND cpt_code = @code AND facility_id IS NULL
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("year", year);
        cmd.Parameters.AddWithValue("code", normalized);
        var affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    public async Task<IReadOnlyList<CptMasterCmsRow>> ListCptMasterCmsAsync(
        Guid tenantId, short year, int limit, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        // Effective RVU comes straight from the credit path's overlay so the management
        // view's "Effective" column can never disagree with what reconciliation credits.
        var master = await LoadMasterForYearsAsync(conn, tx, tenantId, new[] { year }, cancellationToken);

        // The CPT master's curated base rows (the display list). Each reader scoped in its own
        // block — Npgsql permits one active reader per connection.
        var amber = new List<(string Code, string Description, decimal Work)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT cpt_code, description, work_rvu
                FROM billing.cpt_codes
                WHERE tenant_id = @t AND year = @year AND is_active = TRUE
                ORDER BY cpt_code
                LIMIT @limit
                """;
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("year", year);
            cmd.Parameters.AddWithValue("limit", limit);
            await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await rdr.ReadAsync(cancellationToken))
                amber.Add((rdr.GetString(0), rdr.GetString(1), rdr.GetDecimal(2)));
        }

        // Raw CMS per-HCPCS truth: latest quarter, global modifier, ALL statuses (so we can
        // tell "status-gated" apart from "differs"). Keyed by normalized HCPCS.
        var cms = new Dictionary<string, (decimal Work, string? Status)>(StringComparer.Ordinal);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT DISTINCT ON (hcpcs) hcpcs, work_rvu, status_code
                FROM billing.rvu_values
                WHERE tenant_id = @t AND year = @year AND modifier = ''
                ORDER BY hcpcs, quarter DESC
                """;
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("year", year);
            await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await rdr.ReadAsync(cancellationToken))
                cms[NormalizeCpt(rdr.GetString(0))] = (rdr.GetDecimal(1), rdr.IsDBNull(2) ? null : rdr.GetString(2));
        }

        // Tenant-wide overrides, keyed by normalized code (single HCPCS or bundle string).
        var overrides = new Dictionary<string, decimal>(StringComparer.Ordinal);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT cpt_code, override_work_rvu
                FROM billing.rvu_overrides
                WHERE tenant_id = @t AND year = @year AND facility_id IS NULL
                """;
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("year", year);
            await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await rdr.ReadAsync(cancellationToken))
            {
                // Key by the same canonical form the credit path uses (set-key for bundles)
                // so the display matches whatever reconciliation credits. Re-canonicalize on
                // read too, in case a legacy row was stored before write-canonicalization.
                var oc = rdr.GetString(0);
                var okey = oc.Contains(';')
                    ? NormalizeCptSetKey(oc.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    : NormalizeCpt(oc);
                overrides[okey] = rdr.GetDecimal(1);
            }
        }

        await tx.CommitAsync(cancellationToken);

        var rows = new List<CptMasterCmsRow>(amber.Count);
        foreach (var (code, description, masterWork) in amber)
        {
            var isBundle = code.Contains(';');
            var normalized = NormalizeCpt(code);
            var parts = isBundle
                ? code.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                : null;
            var setKey = isBundle ? NormalizeCptSetKey(parts!) : null;

            // Look up the override by the SAME key the credit path uses — set-key for a bundle,
            // NormalizeCpt for a single — so the Override column never reads null while the
            // override is in effect (and being credited via EffectiveWorkRvu).
            var overrideKey = setKey ?? normalized;
            decimal? overrideRvu = overrides.TryGetValue(overrideKey, out var ov) ? ov : null;

            decimal effective;
            decimal? cmsRvu = null;
            string? cmsStatus = null;
            int? bundleParts = null, bundleMatched = null;
            string verdict;

            if (!isBundle)
            {
                effective = master.Singletons.TryGetValue((year, normalized), out var m) ? m.WorkRvu : masterWork;
                if (cms.TryGetValue(normalized, out var c))
                {
                    cmsRvu = c.Work;
                    cmsStatus = c.Status;
                    // Mirror the credit overlay's gate exactly (status 'A' AND work > 0). A
                    // status-'A' row with work 0 is NOT overlaid, so don't imply CMS drives the
                    // credit — label it gated like the other non-credited rows.
                    verdict = (!string.Equals(c.Status, "A", StringComparison.OrdinalIgnoreCase) || c.Work <= 0m)
                        ? "status_gated"
                        : c.Work == masterWork ? "matches" : "differs";
                }
                else
                {
                    verdict = "not_in_cms";
                }
            }
            else
            {
                bundleParts = parts!.Length;
                decimal sum = 0m;
                int matched = 0;
                foreach (var p in parts!)
                    if (cms.TryGetValue(NormalizeCpt(p), out var c)) { sum += c.Work; matched++; }
                bundleMatched = matched;

                effective = master.Bundles.TryGetValue((year, setKey!), out var mb) ? mb.WorkRvu : masterWork;

                if (matched < parts!.Length)
                {
                    cmsRvu = matched > 0 ? sum : null;
                    verdict = "partial";
                }
                else
                {
                    cmsRvu = sum;
                    verdict = sum == masterWork ? "matches_sum" : "differs_sum";
                }
            }

            rows.Add(new CptMasterCmsRow(
                Year: year, Code: code, IsBundle: isBundle, Description: description,
                MasterWorkRvu: masterWork, CmsWorkRvu: cmsRvu, CmsStatus: cmsStatus,
                BundleParts: bundleParts, BundleMatched: bundleMatched,
                OverrideWorkRvu: overrideRvu, EffectiveWorkRvu: effective, Verdict: verdict));
        }
        return rows;
    }

    private static RvuOverride ReadOverride(NpgsqlDataReader rdr) => new(
        OverrideId:      rdr.GetInt64(0),
        Year:            rdr.GetInt16(1),
        Code:            rdr.GetString(2),
        FacilityId:      rdr.IsDBNull(3) ? null : rdr.GetInt64(3),
        OverrideWorkRvu: rdr.GetDecimal(4),
        Note:            rdr.IsDBNull(5) ? null : rdr.GetString(5),
        CreatedByUserId: rdr.IsDBNull(6) ? null : rdr.GetGuid(6),
        CreatedAt:       new DateTimeOffset(rdr.GetDateTime(7), TimeSpan.Zero),
        UpdatedAt:       new DateTimeOffset(rdr.GetDateTime(8), TimeSpan.Zero));

    public async Task<IReadOnlyList<CptCode>> ListCptCodesAsync(
        Guid tenantId, short year, string? search, int limit, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        var hasSearch = !string.IsNullOrWhiteSpace(search);
        cmd.CommandText = $"""
            SELECT year, cpt_code, description, work_rvu, notes, is_active,
                   imported_from_import_id, created_at, updated_at
            FROM billing.cpt_codes
            WHERE tenant_id = @t
              AND year = @year
              AND is_active = TRUE
              {(hasSearch ? "AND (description ILIKE @q OR cpt_code ILIKE @q)" : "")}
            ORDER BY cpt_code
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("year", year);
        cmd.Parameters.AddWithValue("limit", limit);
        if (hasSearch) cmd.Parameters.AddWithValue("q", $"%{search}%");

        var result = new List<CptCode>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new CptCode(
                Year: reader.GetInt16(0),
                Code: reader.GetString(1),
                Description: reader.GetString(2),
                WorkRvu: reader.GetDecimal(3),
                Notes: reader.IsDBNull(4) ? null : reader.GetString(4),
                IsActive: reader.GetBoolean(5),
                ImportedFromImportId: reader.IsDBNull(6) ? null : reader.GetInt64(6),
                CreatedAt: new DateTimeOffset(reader.GetDateTime(7), TimeSpan.Zero),
                UpdatedAt: new DateTimeOffset(reader.GetDateTime(8), TimeSpan.Zero)));
        }
        return result;
    }

    public async Task<CptCodeChange> UpdateCptCodeAsync(
        Guid tenantId, short year, string code,
        decimal? workRvu, string? description, string? notes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        // Read the existing row inside the transaction so the audit "before"
        // matches what we actually overwrote.
        CptCode? before = null;
        await using (var selectCmd = conn.CreateCommand())
        {
            selectCmd.Transaction = tx;
            selectCmd.CommandText = """
                SELECT year, cpt_code, description, work_rvu, notes, is_active,
                       imported_from_import_id, created_at, updated_at
                FROM billing.cpt_codes
                WHERE tenant_id=@t AND year=@year AND cpt_code=@code
                FOR UPDATE
                """;
            selectCmd.Parameters.AddWithValue("t", tenantId);
            selectCmd.Parameters.AddWithValue("year", year);
            selectCmd.Parameters.AddWithValue("code", code);
            await using var reader = await selectCmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                before = new CptCode(
                    Year: reader.GetInt16(0),
                    Code: reader.GetString(1),
                    Description: reader.GetString(2),
                    WorkRvu: reader.GetDecimal(3),
                    Notes: reader.IsDBNull(4) ? null : reader.GetString(4),
                    IsActive: reader.GetBoolean(5),
                    ImportedFromImportId: reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    CreatedAt: new DateTimeOffset(reader.GetDateTime(7), TimeSpan.Zero),
                    UpdatedAt: new DateTimeOffset(reader.GetDateTime(8), TimeSpan.Zero));
            }
        }
        if (before is null)
            throw new KeyNotFoundException($"No CPT row for tenant {tenantId} year {year} code '{code}'.");

        // No-op short-circuit so we don't burn an audit row on an unchanged save.
        if (workRvu is null && description is null && notes is null)
        {
            await tx.CommitAsync(cancellationToken);
            return new CptCodeChange(before, before);
        }

        await using (var updateCmd = conn.CreateCommand())
        {
            updateCmd.Transaction = tx;
            updateCmd.CommandText = """
                UPDATE billing.cpt_codes SET
                    work_rvu    = COALESCE(@rvu, work_rvu),
                    description = COALESCE(@desc, description),
                    notes       = CASE WHEN @notes_set THEN @notes ELSE notes END,
                    updated_at  = NOW()
                WHERE tenant_id=@t AND year=@year AND cpt_code=@code
                """;
            updateCmd.Parameters.AddWithValue("t", tenantId);
            updateCmd.Parameters.AddWithValue("year", year);
            updateCmd.Parameters.AddWithValue("code", code);
            updateCmd.Parameters.AddWithValue("rvu", (object?)workRvu ?? DBNull.Value);
            updateCmd.Parameters.AddWithValue("desc", (object?)description ?? DBNull.Value);
            // Notes is a TEXT column — null is a legal value, so we need an
            // explicit "did the caller intend to write notes" flag to tell
            // null-the-string from null-the-no-op.
            updateCmd.Parameters.AddWithValue("notes_set", notes is not null);
            updateCmd.Parameters.AddWithValue("notes", (object?)notes ?? DBNull.Value);
            await updateCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        CptCode after;
        await using (var afterCmd = conn.CreateCommand())
        {
            afterCmd.Transaction = tx;
            afterCmd.CommandText = """
                SELECT year, cpt_code, description, work_rvu, notes, is_active,
                       imported_from_import_id, created_at, updated_at
                FROM billing.cpt_codes
                WHERE tenant_id=@t AND year=@year AND cpt_code=@code
                """;
            afterCmd.Parameters.AddWithValue("t", tenantId);
            afterCmd.Parameters.AddWithValue("year", year);
            afterCmd.Parameters.AddWithValue("code", code);
            await using var reader = await afterCmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Row vanished mid-transaction.");
            after = new CptCode(
                Year: reader.GetInt16(0),
                Code: reader.GetString(1),
                Description: reader.GetString(2),
                WorkRvu: reader.GetDecimal(3),
                Notes: reader.IsDBNull(4) ? null : reader.GetString(4),
                IsActive: reader.GetBoolean(5),
                ImportedFromImportId: reader.IsDBNull(6) ? null : reader.GetInt64(6),
                CreatedAt: new DateTimeOffset(reader.GetDateTime(7), TimeSpan.Zero),
                UpdatedAt: new DateTimeOffset(reader.GetDateTime(8), TimeSpan.Zero));
        }

        await tx.CommitAsync(cancellationToken);
        return new CptCodeChange(before, after);
    }

    public async Task<IReadOnlyList<CptImport>> ListRecentImportsAsync(
        Guid tenantId, int limit, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT import_id, file_name, sheet_name, year,
                   parsed_rows, inserted_rows, updated_rows, skipped_rows,
                   errors, ran_by_user_id, ran_at
            FROM billing.cpt_imports
            WHERE tenant_id = @t
            ORDER BY ran_at DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("limit", limit);

        var result = new List<CptImport>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var errorsRaw = reader.IsDBNull(8) ? "[]" : reader.GetString(8);
            var errors = JsonSerializer.Deserialize<List<CptImportError>>(errorsRaw) ?? new();
            result.Add(new CptImport(
                ImportId: reader.GetInt64(0),
                FileName: reader.GetString(1),
                SheetName: reader.GetString(2),
                Year: reader.GetInt16(3),
                ParsedRows: reader.GetInt32(4),
                InsertedRows: reader.GetInt32(5),
                UpdatedRows: reader.GetInt32(6),
                SkippedRows: reader.GetInt32(7),
                Errors: errors,
                RanByUserId: reader.GetGuid(9),
                RanAt: new DateTimeOffset(reader.GetDateTime(10), TimeSpan.Zero)));
        }
        return result;
    }

    public async Task<IReadOnlyList<RvuImport>> ListRecentRvuImportsAsync(
        Guid tenantId, int limit, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT import_id, file_name, year, quarter,
                   parsed_rows, inserted_rows, updated_rows, skipped_rows,
                   errors, ran_by_user_id, ran_at
            FROM billing.rvu_imports
            WHERE tenant_id = @t
            ORDER BY ran_at DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("limit", limit);

        var result = new List<RvuImport>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var errorsRaw = reader.IsDBNull(8) ? "[]" : reader.GetString(8);
            var errors = JsonSerializer.Deserialize<List<CptImportError>>(errorsRaw) ?? new();
            result.Add(new RvuImport(
                ImportId: reader.GetInt64(0),
                FileName: reader.GetString(1),
                Year: reader.GetInt16(2),
                Quarter: reader.GetString(3)[0],
                ParsedRows: reader.GetInt32(4),
                InsertedRows: reader.GetInt32(5),
                UpdatedRows: reader.GetInt32(6),
                SkippedRows: reader.GetInt32(7),
                Errors: errors,
                RanByUserId: reader.GetGuid(9),
                RanAt: new DateTimeOffset(reader.GetDateTime(10), TimeSpan.Zero)));
        }
        return result;
    }

    // ========================================================================
    // Reconciliation
    // ========================================================================

    public async Task<ReconciliationRun> RunReconciliationAsync(
        Guid tenantId,
        Guid runByUserId,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        long? facilityId,
        short runKind,
        IReadOnlyList<SignedProcedureLineItem> source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (periodEnd <= periodStart)
            throw new ArgumentException("periodEnd must be strictly after periodStart.", nameof(periodEnd));
        if (runKind != 1 && runKind != 2)
            throw new ArgumentOutOfRangeException(nameof(runKind), runKind, "runKind must be 1 (Preview) or 2 (Final).");

        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        // 1. Load the CPT master for every year touched by the source rows.
        //    Run is intra-year in the common case, but bridge windows pay for two.
        var yearsNeeded = source.Select(s => (short)s.SignedAt.Year).Distinct().ToArray();
        var master = await LoadMasterForYearsAsync(conn, tx, tenantId, yearsNeeded, cancellationToken);

        // 2. Pre-load the site_code → facility_id map for the tenant so each
        //    persisted line item can carry the local facility_id alongside the
        //    Novarad site_code snapshot.
        var facilityBySite = await LoadFacilityMapAsync(conn, tx, tenantId, cancellationToken);

        // 2b. Load approved service_code → cpt_code mappings. The matcher applies
        //     these before bundle/singleton lookup so mapped codes start crediting.
        var crosswalk = await LoadCrosswalkAsync(conn, tx, tenantId, cancellationToken);

        // 3. Run the matcher in memory — pure CPU, no DB.
        var (aggregated, notes, appliedCrosswalkCodes) =
            MatchAndAggregate(source, master, crosswalk, facilityBySite);

        // 4. Compute run-level rollups from the aggregated line items.
        int totalReports = aggregated.SelectMany(a => a.ReportIds).Distinct().Count();
        int totalRads    = aggregated.Select(a => a.NovaradPhysicianId).Distinct().Count();
        decimal totalRvu = aggregated.Sum(a => a.WorkRvuTotal);

        // 4b. Per-facility subtotals + run-level STAT count (distinct credited
        //     reports flagged STAT). Computed over the same credited population as
        //     totalReports, so the per-facility subtotals reconcile to the run total.
        var (facilitySummaries, statReportCount) = BuildFacilityRollups(
            aggregated.Select(a => (a.SiteCode, a.FacilityId, a.ReportIds, a.StatReportIds)));

        // 5. Insert run header.
        long runId;
        DateTimeOffset generatedAt;
        await using (var insertRun = conn.CreateCommand())
        {
            insertRun.Transaction = tx;
            insertRun.CommandText = """
                INSERT INTO billing.reconciliation_runs
                    (tenant_id, period_start, period_end, facility_id, run_kind,
                     total_reports, total_radiologists, total_work_rvu, stat_report_count,
                     notes, generated_by_user_id)
                VALUES (@t, @ps, @pe, @fac, @kind, @reports, @rads, @rvu, @stat,
                        @notes::jsonb, @user)
                RETURNING run_id, generated_at
                """;
            insertRun.Parameters.AddWithValue("t", tenantId);
            insertRun.Parameters.AddWithValue("ps", periodStart.UtcDateTime);
            insertRun.Parameters.AddWithValue("pe", periodEnd.UtcDateTime);
            insertRun.Parameters.AddWithValue("fac", (object?)facilityId ?? DBNull.Value);
            insertRun.Parameters.AddWithValue("kind", runKind);
            insertRun.Parameters.AddWithValue("reports", totalReports);
            insertRun.Parameters.AddWithValue("rads", totalRads);
            insertRun.Parameters.AddWithValue("rvu", totalRvu);
            insertRun.Parameters.AddWithValue("stat", statReportCount);
            insertRun.Parameters.AddWithValue("notes", JsonSerializer.Serialize(notes));
            insertRun.Parameters.AddWithValue("user", runByUserId);
            await using var rdr = await insertRun.ExecuteReaderAsync(cancellationToken);
            if (!await rdr.ReadAsync(cancellationToken))
                throw new InvalidOperationException("INSERT reconciliation_runs did not return run_id.");
            runId = rdr.GetInt64(0);
            generatedAt = new DateTimeOffset(rdr.GetDateTime(1), TimeSpan.Zero);
        }

        // 6. Insert line items. N is small (a few hundred at most) — a simple
        //    parameterized loop on a single prepared command is fine and keeps
        //    the bigint[] array-per-row easy to express.
        var persisted = new List<ReconciliationLineItem>(aggregated.Count);
        if (aggregated.Count > 0)
        {
            await using var insertLine = conn.CreateCommand();
            insertLine.Transaction = tx;
            insertLine.CommandText = """
                INSERT INTO billing.reconciliation_line_items
                    (run_id, tenant_id, novarad_physician_id, physician_display_name,
                     site_code, facility_id, cpt_code, cpt_description,
                     report_count, units, work_rvu_per_unit, work_rvu_total,
                     novarad_rvu_work, rvu_mismatch, novarad_report_ids,
                     novarad_stat_report_ids)
                VALUES (@run, @t, @phys, @phys_name, @site, @fac, @cpt, @desc,
                        @reports, @units, @rvu_per, @rvu_total, @nv_rvu, @mismatch, @rids,
                        @stat_rids)
                RETURNING line_id
                """;
            insertLine.Parameters.Add(new NpgsqlParameter("run", NpgsqlDbType.Bigint));
            insertLine.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.Uuid));
            insertLine.Parameters.Add(new NpgsqlParameter("phys", NpgsqlDbType.Bigint));
            insertLine.Parameters.Add(new NpgsqlParameter("phys_name", NpgsqlDbType.Text));
            insertLine.Parameters.Add(new NpgsqlParameter("site", NpgsqlDbType.Text));
            insertLine.Parameters.Add(new NpgsqlParameter("fac", NpgsqlDbType.Bigint));
            insertLine.Parameters.Add(new NpgsqlParameter("cpt", NpgsqlDbType.Text));
            insertLine.Parameters.Add(new NpgsqlParameter("desc", NpgsqlDbType.Text));
            insertLine.Parameters.Add(new NpgsqlParameter("reports", NpgsqlDbType.Integer));
            insertLine.Parameters.Add(new NpgsqlParameter("units", NpgsqlDbType.Numeric));
            insertLine.Parameters.Add(new NpgsqlParameter("rvu_per", NpgsqlDbType.Numeric));
            insertLine.Parameters.Add(new NpgsqlParameter("rvu_total", NpgsqlDbType.Numeric));
            insertLine.Parameters.Add(new NpgsqlParameter("nv_rvu", NpgsqlDbType.Numeric));
            insertLine.Parameters.Add(new NpgsqlParameter("mismatch", NpgsqlDbType.Boolean));
            insertLine.Parameters.Add(new NpgsqlParameter("rids", NpgsqlDbType.Array | NpgsqlDbType.Bigint));
            insertLine.Parameters.Add(new NpgsqlParameter("stat_rids", NpgsqlDbType.Array | NpgsqlDbType.Bigint));
            await insertLine.PrepareAsync(cancellationToken);

            foreach (var agg in aggregated)
            {
                insertLine.Parameters["run"].Value       = runId;
                insertLine.Parameters["t"].Value         = tenantId;
                insertLine.Parameters["phys"].Value      = agg.NovaradPhysicianId;
                insertLine.Parameters["phys_name"].Value = agg.PhysicianDisplayName;
                insertLine.Parameters["site"].Value      = agg.SiteCode;
                insertLine.Parameters["fac"].Value       = (object?)agg.FacilityId ?? DBNull.Value;
                insertLine.Parameters["cpt"].Value       = agg.CptCode;
                insertLine.Parameters["desc"].Value      = (object?)agg.CptDescription ?? DBNull.Value;
                insertLine.Parameters["reports"].Value   = agg.ReportCount;
                insertLine.Parameters["units"].Value     = agg.Units;
                insertLine.Parameters["rvu_per"].Value   = agg.WorkRvuPerUnit;
                insertLine.Parameters["rvu_total"].Value = agg.WorkRvuTotal;
                insertLine.Parameters["nv_rvu"].Value    = (object?)agg.NovaradRvuWork ?? DBNull.Value;
                insertLine.Parameters["mismatch"].Value  = agg.RvuMismatch;
                insertLine.Parameters["rids"].Value      = agg.ReportIds.ToArray();
                insertLine.Parameters["stat_rids"].Value = agg.StatReportIds.ToArray();
                var lineIdObj = await insertLine.ExecuteScalarAsync(cancellationToken)
                    ?? throw new InvalidOperationException("INSERT reconciliation_line_items did not return line_id.");
                var lineId = (long)lineIdObj;
                persisted.Add(new ReconciliationLineItem(
                    LineId: lineId,
                    NovaradPhysicianId: agg.NovaradPhysicianId,
                    PhysicianDisplayName: agg.PhysicianDisplayName,
                    SiteCode: agg.SiteCode,
                    FacilityId: agg.FacilityId,
                    CptCode: agg.CptCode,
                    CptDescription: agg.CptDescription,
                    ReportCount: agg.ReportCount,
                    Units: agg.Units,
                    WorkRvuPerUnit: agg.WorkRvuPerUnit,
                    WorkRvuTotal: agg.WorkRvuTotal,
                    NovaradRvuWork: agg.NovaradRvuWork,
                    RvuMismatch: agg.RvuMismatch,
                    NovaradReportIds: agg.ReportIds,
                    NovaradStatReportIds: agg.StatReportIds));
            }
        }

        // 7. Crosswalk telemetry — bump applied_count + last_used_at on the
        //    rows that actually fired this run. Lets Amber spot stale mappings
        //    in the management UI.
        if (appliedCrosswalkCodes.Count > 0)
        {
            await using var bump = conn.CreateCommand();
            bump.Transaction = tx;
            bump.CommandText = """
                UPDATE billing.service_code_crosswalk
                SET applied_count = applied_count + 1,
                    last_used_at  = LOCALTIMESTAMP
                WHERE tenant_id = @t AND service_code = ANY(@codes)
                """;
            bump.Parameters.AddWithValue("t", tenantId);
            bump.Parameters.Add(new NpgsqlParameter("codes", NpgsqlDbType.Array | NpgsqlDbType.Text)
                { Value = appliedCrosswalkCodes.ToArray() });
            await bump.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);

        return new ReconciliationRun(
            RunId: runId,
            PeriodStart: periodStart,
            PeriodEnd: periodEnd,
            FacilityId: facilityId,
            RunKind: runKind,
            TotalReports: totalReports,
            TotalRadiologists: totalRads,
            TotalWorkRvu: totalRvu,
            StatReportCount: statReportCount,
            LineItems: persisted,
            Notes: notes,
            FacilitySummaries: facilitySummaries,
            GeneratedByUserId: runByUserId,
            GeneratedAt: generatedAt);
    }

    public async Task<IReadOnlyList<UnmappedServiceCode>> BuildUnmappedReportAsync(
        Guid tenantId,
        IReadOnlyList<SignedProcedureLineItem> source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count == 0) return Array.Empty<UnmappedServiceCode>();

        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        MasterIndex master;
        IReadOnlyDictionary<string, string> crosswalk;
        Dictionary<string, long> facilityBySite;
        await using (var tx = await conn.BeginTransactionAsync(cancellationToken))
        {
            var years = source.Select(s => (short)s.SignedAt.Year).Distinct().ToArray();
            master = await LoadMasterForYearsAsync(conn, tx, tenantId, years, cancellationToken);
            // Apply the crosswalk to the unmapped report as well, so codes with an
            // approved mapping disappear from the list once the matcher would credit
            // them. Suppressed (status=2) rows are absent from the dict and therefore
            // stay on the report — exactly the desired semantics.
            crosswalk = await LoadCrosswalkAsync(conn, tx, tenantId, cancellationToken);
            // site_code → facility_id so each per-site breakdown carries the local id
            // (usually null — the customer's Novarad site_codes aren't all mapped).
            facilityBySite = await LoadFacilityMapAsync(conn, tx, tenantId, cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }

        // Per uncredited (year, code): distinct reports, line count, a representative
        // Novarad description, and a per-site breakdown.
        var acc = new Dictionary<(short Year, string Code), UnmappedAccum>();

        foreach (var procGroup in source.GroupBy(s => (s.ReportId, s.ProcedureId)))
        {
            var rows = procGroup.ToList();
            var year = (short)rows[0].SignedAt.Year;
            // Resolve via crosswalk BEFORE computing distinct/setKey so a procedure
            // now bundle-credited via a mapped CPT no longer leaks into the report.
            var distinct = rows.Select(r => ResolveCode(r.CptCode, crosswalk).ResolvedCode).Distinct().ToArray();
            var setKey = NormalizeCptSetKey(distinct);

            // Procedure credited as a bundle → nothing uncredited here.
            if (distinct.Length >= 2 && master.Bundles.ContainsKey((year, setKey)))
                continue;

            foreach (var row in rows)
            {
                var (code, _) = ResolveCode(row.CptCode, crosswalk);
                if (master.Singletons.ContainsKey((year, code)))
                    continue; // credited as a singleton (possibly via crosswalk)

                var key = (year, code);
                if (!acc.TryGetValue(key, out var e))
                    acc[key] = e = new UnmappedAccum();
                e.Reports.Add(row.ReportId);
                e.Lines += 1;
                if (e.Desc is null && row.CptDescription is not null) e.Desc = row.CptDescription;

                if (!e.BySite.TryGetValue(row.SiteCode, out var site))
                    e.BySite[row.SiteCode] = site = new UnmappedSiteAccum();
                site.Reports.Add(row.ReportId);
                site.Lines += 1;
            }
        }

        if (acc.Count == 0) return Array.Empty<UnmappedServiceCode>();

        // Best CPT-master match per distinct code (one batched query), surfaced inline
        // so the user sees a candidate CPT + RVU without opening the Map dialog.
        var suggestionByCode = await LoadBestSuggestionsAsync(conn, tenantId, master, acc, cancellationToken);

        return acc
            .Select(kv =>
            {
                var (year, code) = kv.Key;
                var a = kv.Value;
                var looks = LooksLikeCpt(code);
                var facilities = a.BySite
                    .Select(s => new UnmappedFacilityBreakdown(
                        SiteCode: s.Key,
                        FacilityId: facilityBySite.TryGetValue(s.Key, out var fid) ? fid : null,
                        ReportCount: s.Value.Reports.Count,
                        ServiceLineCount: s.Value.Lines))
                    .OrderByDescending(f => f.ReportCount)
                    .ThenBy(f => f.SiteCode, StringComparer.Ordinal)
                    .ToList();
                suggestionByCode.TryGetValue(code, out var sug);
                return new UnmappedServiceCode(
                    Code: code,
                    Year: year,
                    Kind: looks ? "cpt_missing_from_master" : "non_cpt_service_code",
                    Description: a.Desc,
                    ReportCount: a.Reports.Count,
                    ServiceLineCount: a.Lines,
                    LooksLikeCpt: looks,
                    Facilities: facilities,
                    SuggestedCpt: sug?.Cpt,
                    SuggestedCptDescription: sug?.Description,
                    SuggestedWorkRvu: sug?.WorkRvu,
                    SuggestionHitKind: sug?.HitKind);
            })
            .OrderByDescending(u => u.ReportCount)
            .ThenByDescending(u => u.ServiceLineCount)
            .ThenBy(u => u.Code, StringComparer.Ordinal)
            .ToList();
    }

    // Batched "best CPT-master match" for the unmapped report. One round-trip: for
    // each distinct code, the exact suffix-stripped code hit (score 1.0) or the top
    // pg_trgm description-similarity hit, evaluated against the latest master year.
    private static async Task<Dictionary<string, SuggestionRow>> LoadBestSuggestionsAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        MasterIndex master,
        Dictionary<(short Year, string Code), UnmappedAccum> acc,
        CancellationToken ct)
    {
        var result = new Dictionary<string, SuggestionRow>(StringComparer.Ordinal);

        // Latest year that actually has master rows; without a master there's nothing
        // to suggest (mirrors SuggestCrosswalkAsync's MAX-year fallback).
        short? year = master.Singletons.Keys.Select(k => (short?)k.Year)
            .Concat(master.Bundles.Keys.Select(k => (short?)k.Year))
            .DefaultIfEmpty(null)
            .Max();
        if (year is null) return result;

        // One representative description + exact-match candidate per distinct code.
        var byCode = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var kv in acc)
            if (!byCode.TryGetValue(kv.Key.Code, out var d) || d is null)
                byCode[kv.Key.Code] = kv.Value.Desc;

        var codes  = byCode.Keys.ToArray();
        var exacts = codes.Select(ExactMatchCandidate).ToArray();
        var descrs = codes.Select(c => byCode[c] ?? "").ToArray();

        const decimal SimThreshold = 0.15m;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT t.code, best.cpt_code, best.description, best.work_rvu, best.hit_kind
            FROM UNNEST(@codes, @exacts, @descrs) AS t(code, exact, descr)
            LEFT JOIN LATERAL (
                SELECT c.cpt_code, c.description, c.work_rvu,
                       CASE WHEN c.cpt_code = t.exact THEN 'exact_code' ELSE 'description' END AS hit_kind
                FROM billing.cpt_codes c
                WHERE c.tenant_id = @t AND c.year = @y AND c.is_active = TRUE
                  AND (c.cpt_code = t.exact
                       OR (NULLIF(t.descr, '') IS NOT NULL AND similarity(c.description, t.descr) >= @th))
                ORDER BY (CASE WHEN c.cpt_code = t.exact THEN 1.0::real
                               ELSE similarity(c.description, t.descr) END) DESC,
                         c.cpt_code
                LIMIT 1
            ) best ON TRUE
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("y", year.Value);
        cmd.Parameters.AddWithValue("th", SimThreshold);
        cmd.Parameters.Add(new NpgsqlParameter("codes", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = codes });
        cmd.Parameters.Add(new NpgsqlParameter("exacts", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = exacts });
        cmd.Parameters.Add(new NpgsqlParameter("descrs", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = descrs });

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            if (rdr.IsDBNull(1)) continue;  // LEFT JOIN LATERAL miss → no match for this code
            result[rdr.GetString(0)] = new SuggestionRow(
                Cpt:         rdr.GetString(1),
                Description: rdr.IsDBNull(2) ? null : rdr.GetString(2),
                WorkRvu:     rdr.GetDecimal(3),
                HitKind:     rdr.GetString(4));
        }
        return result;
    }

    // Suffix-strip an exact-match candidate (e.g. "71045-26" → "71045") only when the
    // base looks like a CPT; otherwise the code itself (a Missing-CPT code won't be in
    // the master, so the exact branch simply misses and description-similarity takes over).
    private static string ExactMatchCandidate(string code)
    {
        var dash = code.IndexOf('-');
        if (dash > 0)
        {
            var baseCode = code[..dash];
            if (LooksLikeCpt(baseCode)) return baseCode;
        }
        return code;
    }

    private sealed class UnmappedAccum
    {
        public HashSet<long> Reports { get; } = new();
        public int Lines { get; set; }
        public string? Desc { get; set; }
        public Dictionary<string, UnmappedSiteAccum> BySite { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class UnmappedSiteAccum
    {
        public HashSet<long> Reports { get; } = new();
        public int Lines { get; set; }
    }

    private sealed record SuggestionRow(string Cpt, string? Description, decimal WorkRvu, string HitKind);

    // ========================================================================
    // Crosswalk (Phase 2): list / get / upsert / set-status / bulk / suggest
    // ========================================================================

    // Full projection used by list/get/upsert/set-status reads. Joins identity.users
    // so the UI can render created_by_display_name without a second round-trip.
    private const string CrosswalkProjection = """
        SELECT x.crosswalk_id, x.service_code, x.cpt_code, x.status, x.source,
               x.note, x.approved_for_description, x.applied_count, x.last_used_at,
               x.created_by_user_id, u.display_name AS created_by_display_name,
               x.updated_by_user_id, x.created_at, x.updated_at
        FROM billing.service_code_crosswalk x
        LEFT JOIN identity.users u ON u.user_id = x.created_by_user_id
        """;

    private static ServiceCodeMapping ReadCrosswalk(NpgsqlDataReader r) => new(
        CrosswalkId:            r.GetInt64(0),
        ServiceCode:            r.GetString(1),
        CptCode:                r.GetString(2),
        Status:                 r.GetInt16(3),
        Source:                 r.GetInt16(4),
        Note:                   r.IsDBNull(5) ? null : r.GetString(5),
        ApprovedForDescription: r.IsDBNull(6) ? null : r.GetString(6),
        AppliedCount:           r.GetInt64(7),
        LastUsedAt:             r.IsDBNull(8) ? null : new DateTimeOffset(r.GetDateTime(8), TimeSpan.Zero),
        CreatedByUserId:        r.GetGuid(9),
        CreatedByDisplayName:   r.IsDBNull(10) ? null : r.GetString(10),
        UpdatedByUserId:        r.IsDBNull(11) ? null : r.GetGuid(11),
        CreatedAt:              new DateTimeOffset(r.GetDateTime(12), TimeSpan.Zero),
        UpdatedAt:              new DateTimeOffset(r.GetDateTime(13), TimeSpan.Zero));

    public async Task<IReadOnlyList<ServiceCodeMapping>> ListCrosswalkAsync(
        Guid tenantId, short? status, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = CrosswalkProjection + """

            WHERE x.tenant_id = @t
              AND (@status::smallint IS NULL OR x.status = @status::smallint)
            ORDER BY x.updated_at DESC
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Smallint)
            { Value = (object?)status ?? DBNull.Value });
        var list = new List<ServiceCodeMapping>();
        await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await rdr.ReadAsync(cancellationToken)) list.Add(ReadCrosswalk(rdr));
        return list;
    }

    public async Task<ServiceCodeMapping?> GetCrosswalkAsync(
        Guid tenantId, string serviceCode, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = CrosswalkProjection + """

            WHERE x.tenant_id = @t AND x.service_code = @sc
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("sc", NormalizeCpt(serviceCode));
        await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
        return await rdr.ReadAsync(cancellationToken) ? ReadCrosswalk(rdr) : null;
    }

    public async Task<CrosswalkUpsertResult> UpsertCrosswalkAsync(
        Guid tenantId, Guid userId, ServiceCodeMappingUpsert upsert,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upsert);
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        long crosswalkId;
        bool inserted;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            // approved_for_description is preserved across UPDATEs unless the caller
            // sends a non-null replacement — the snapshot at first approval is the
            // load-bearing audit artifact; later edits shouldn't silently overwrite it.
            cmd.CommandText = """
                INSERT INTO billing.service_code_crosswalk
                    (tenant_id, service_code, cpt_code, status, source, note,
                     approved_for_description, created_by_user_id, updated_by_user_id)
                VALUES (@t, @sc, @cpt, COALESCE(@status, 1::smallint), @source, @note, @afd, @u, @u)
                ON CONFLICT (tenant_id, service_code) DO UPDATE
                    SET cpt_code                 = EXCLUDED.cpt_code,
                        status                   = EXCLUDED.status,
                        source                   = EXCLUDED.source,
                        note                     = EXCLUDED.note,
                        approved_for_description = COALESCE(EXCLUDED.approved_for_description,
                                                            billing.service_code_crosswalk.approved_for_description),
                        updated_by_user_id       = EXCLUDED.updated_by_user_id,
                        updated_at               = NOW()
                RETURNING crosswalk_id, (xmax = 0) AS inserted
                """;
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("sc", NormalizeCpt(upsert.ServiceCode));
            cmd.Parameters.AddWithValue("cpt", NormalizeCpt(upsert.CptCode));
            cmd.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Smallint)
                { Value = (object?)upsert.Status ?? DBNull.Value });
            cmd.Parameters.AddWithValue("source", upsert.Source);
            cmd.Parameters.Add(new NpgsqlParameter("note", NpgsqlDbType.Text)
                { Value = (object?)upsert.Note ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("afd", NpgsqlDbType.Text)
                { Value = (object?)upsert.ApprovedForDescription ?? DBNull.Value });
            cmd.Parameters.AddWithValue("u", userId);
            await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await rdr.ReadAsync(cancellationToken))
                throw new InvalidOperationException("INSERT service_code_crosswalk did not return a row.");
            crosswalkId = rdr.GetInt64(0);
            inserted = rdr.GetBoolean(1);
        }

        ServiceCodeMapping mapping;
        await using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = CrosswalkProjection + "\nWHERE x.crosswalk_id = @id";
            read.Parameters.AddWithValue("id", crosswalkId);
            await using var rdr = await read.ExecuteReaderAsync(cancellationToken);
            if (!await rdr.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Crosswalk row vanished after upsert.");
            mapping = ReadCrosswalk(rdr);
        }
        await tx.CommitAsync(cancellationToken);
        return new CrosswalkUpsertResult(mapping, inserted);
    }

    public async Task<ServiceCodeMapping> SetCrosswalkStatusAsync(
        Guid tenantId, Guid userId, string serviceCode, short status,
        CancellationToken cancellationToken = default)
    {
        if (status is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(status), status,
                "status must be 1 (approved) or 2 (suppressed).");

        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        long id;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE billing.service_code_crosswalk
                SET status             = @s,
                    updated_by_user_id = @u,
                    updated_at         = NOW()
                WHERE tenant_id = @t AND service_code = @sc
                RETURNING crosswalk_id
                """;
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("sc", NormalizeCpt(serviceCode));
            cmd.Parameters.AddWithValue("s", status);
            cmd.Parameters.AddWithValue("u", userId);
            var idObj = await cmd.ExecuteScalarAsync(cancellationToken);
            if (idObj is null)
                throw new KeyNotFoundException($"No crosswalk row for service_code='{serviceCode}'.");
            id = (long)idObj;
        }
        await using (var read = conn.CreateCommand())
        {
            read.CommandText = CrosswalkProjection + "\nWHERE x.crosswalk_id = @id";
            read.Parameters.AddWithValue("id", id);
            await using var rdr = await read.ExecuteReaderAsync(cancellationToken);
            await rdr.ReadAsync(cancellationToken);
            return ReadCrosswalk(rdr);
        }
    }

    public async Task<BulkImportResult> BulkUpsertCrosswalkAsync(
        Guid tenantId, Guid userId, IReadOnlyList<BulkImportRow> rows,
        bool updateOnConflict, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
            return new BulkImportResult(0, 0, 0, 0, Array.Empty<BulkImportRowResult>());

        var n = rows.Count;
        var scArr = new string[n];
        var cptArr = new string[n];
        var noteArr = new string?[n];
        for (int i = 0; i < n; i++)
        {
            scArr[i]  = NormalizeCpt(rows[i].ServiceCode);
            cptArr[i] = NormalizeCpt(rows[i].CptCode);
            noteArr[i] = rows[i].Note;
        }

        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        // source=3 (bulk). ON CONFLICT branch chosen at runtime — skip vs. update.
        var conflictClause = updateOnConflict
            ? """
              ON CONFLICT (tenant_id, service_code) DO UPDATE
                  SET cpt_code           = EXCLUDED.cpt_code,
                      source             = 3,
                      note               = EXCLUDED.note,
                      updated_by_user_id = EXCLUDED.updated_by_user_id,
                      updated_at         = NOW()
              """
            : "ON CONFLICT (tenant_id, service_code) DO NOTHING";

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $$"""
            WITH new_rows AS (
                SELECT UNNEST(@sc)   AS service_code,
                       UNNEST(@cpt)  AS cpt_code,
                       UNNEST(@note) AS note
            ),
            ins AS (
                INSERT INTO billing.service_code_crosswalk
                    (tenant_id, service_code, cpt_code, status, source, note,
                     created_by_user_id, updated_by_user_id)
                SELECT @t, service_code, cpt_code, 1::smallint, 3::smallint, note, @u, @u
                FROM new_rows
                {{conflictClause}}
                RETURNING service_code, (xmax = 0) AS inserted
            )
            SELECT service_code, inserted FROM ins
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.Add(new NpgsqlParameter("sc", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = scArr });
        cmd.Parameters.Add(new NpgsqlParameter("cpt", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = cptArr });
        cmd.Parameters.Add(new NpgsqlParameter("note", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = noteArr });

        var outcomeByCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using (var rdr = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await rdr.ReadAsync(cancellationToken))
            {
                outcomeByCode[rdr.GetString(0)] = rdr.GetBoolean(1) ? "inserted" : "updated";
            }
        }

        var results = new List<BulkImportRowResult>(n);
        int inserted = 0, updated = 0, skipped = 0;
        for (int i = 0; i < n; i++)
        {
            var sc = scArr[i];
            if (outcomeByCode.TryGetValue(sc, out var outcome))
            {
                results.Add(new BulkImportRowResult(sc, outcome, null));
                if (outcome == "inserted") inserted++; else updated++;
            }
            else
            {
                results.Add(new BulkImportRowResult(sc, "skipped", null));
                skipped++;
            }
        }

        await tx.CommitAsync(cancellationToken);
        return new BulkImportResult(inserted, updated, skipped, 0, results);
    }

    public async Task<IReadOnlyList<CrosswalkSuggestion>> SuggestCrosswalkAsync(
        Guid tenantId, short? year, string serviceCode, string? novaradDescription,
        int limit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceCode);
        limit = Math.Clamp(limit, 1, 50);
        var normalizedSc = NormalizeCpt(serviceCode);

        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);

        // Suppressed mapping → "do not suggest". Caller surfaces the hint.
        await using (var check = conn.CreateCommand())
        {
            check.CommandText = """
                SELECT status FROM billing.service_code_crosswalk
                WHERE tenant_id = @t AND service_code = @sc
                """;
            check.Parameters.AddWithValue("t", tenantId);
            check.Parameters.AddWithValue("sc", normalizedSc);
            var existing = await check.ExecuteScalarAsync(cancellationToken);
            if (existing is short s && s == 2)
                return Array.Empty<CrosswalkSuggestion>();
        }

        // Resolve the year. Caller-supplied wins; otherwise fall back to the
        // latest year in the master so suggestions still work when the unmapped
        // row's signed_date predates the imported sheet.
        short resolvedYear;
        if (year is not null)
        {
            resolvedYear = year.Value;
        }
        else
        {
            await using var yc = conn.CreateCommand();
            yc.CommandText = "SELECT MAX(year) FROM billing.cpt_codes WHERE tenant_id = @t AND is_active = TRUE";
            yc.Parameters.AddWithValue("t", tenantId);
            var maxYear = await yc.ExecuteScalarAsync(cancellationToken);
            if (maxYear is null or DBNull) return Array.Empty<CrosswalkSuggestion>();
            resolvedYear = (short)maxYear;
        }

        // Suffix-stripped exact-code branch only when the base looks like a CPT.
        string? suffixStripped = null;
        var dash = normalizedSc.IndexOf('-');
        if (dash > 0)
        {
            var candidate = normalizedSc[..dash];
            if (LooksLikeCpt(candidate)) suffixStripped = candidate;
        }

        await using var cmd = conn.CreateCommand();
        // Per-tenant masters are small (~500–2k rows), so a seq scan computing
        // similarity() is well under a millisecond. Using `similarity(...) >= @th`
        // sidesteps pg_trgm's default 0.3 threshold (too strict for short
        // descriptions like "X-RAY EXAM OF FINGER(S)" vs. "XR FINGER LEFT THUMB"
        // which score ~0.22). If a master ever grows past ~50k, revisit and put
        // the GIN index back in play via `SET LOCAL pg_trgm.similarity_threshold`.
        const decimal SimThreshold = 0.15m;
        if (suffixStripped is not null && !string.IsNullOrWhiteSpace(novaradDescription))
        {
            cmd.CommandText = """
                (
                  SELECT cpt_code, description, work_rvu, 1.0::numeric AS score, 'exact_code' AS hit_kind
                  FROM billing.cpt_codes
                  WHERE tenant_id = @t AND year = @y AND is_active = TRUE
                    AND cpt_code = @sx
                )
                UNION ALL
                (
                  SELECT cpt_code, description, work_rvu,
                         similarity(description, @q)::numeric AS score,
                         'description' AS hit_kind
                  FROM billing.cpt_codes
                  WHERE tenant_id = @t AND year = @y AND is_active = TRUE
                    AND similarity(description, @q) >= @th
                    AND cpt_code <> @sx
                  ORDER BY similarity(description, @q) DESC
                  LIMIT @lim
                )
                ORDER BY score DESC, cpt_code
                LIMIT @lim
                """;
            cmd.Parameters.AddWithValue("sx", suffixStripped);
            cmd.Parameters.AddWithValue("q", novaradDescription);
            cmd.Parameters.AddWithValue("th", SimThreshold);
        }
        else if (suffixStripped is not null)
        {
            cmd.CommandText = """
                SELECT cpt_code, description, work_rvu, 1.0::numeric AS score, 'exact_code' AS hit_kind
                FROM billing.cpt_codes
                WHERE tenant_id = @t AND year = @y AND is_active = TRUE
                  AND cpt_code = @sx
                """;
            cmd.Parameters.AddWithValue("sx", suffixStripped);
        }
        else if (!string.IsNullOrWhiteSpace(novaradDescription))
        {
            cmd.CommandText = """
                SELECT cpt_code, description, work_rvu,
                       similarity(description, @q)::numeric AS score,
                       'description' AS hit_kind
                FROM billing.cpt_codes
                WHERE tenant_id = @t AND year = @y AND is_active = TRUE
                  AND similarity(description, @q) >= @th
                ORDER BY similarity(description, @q) DESC
                LIMIT @lim
                """;
            cmd.Parameters.AddWithValue("q", novaradDescription);
            cmd.Parameters.AddWithValue("th", SimThreshold);
        }
        else
        {
            return Array.Empty<CrosswalkSuggestion>();
        }

        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("y", resolvedYear);
        cmd.Parameters.AddWithValue("lim", limit);

        var results = new List<CrosswalkSuggestion>(limit);
        await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await rdr.ReadAsync(cancellationToken))
        {
            results.Add(new CrosswalkSuggestion(
                CptCode:     rdr.GetString(0),
                Description: rdr.GetString(1),
                WorkRvu:     rdr.GetDecimal(2),
                Score:       rdr.GetDecimal(3),
                HitKind:     rdr.GetString(4)));
        }
        return results;
    }

    public async Task<IReadOnlyList<long>?> GetReconciliationLineReportIdsAsync(
        Guid tenantId,
        long runId,
        long novaradPhysicianId,
        string cptCode,
        string siteCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cptCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(siteCode);

        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        // Composite natural key (run_id, novarad_physician_id, cpt_code, site_code)
        // identifies a single persisted line. tenant_id is in the WHERE for the
        // RLS-bypass path; with RLS active the tenant_id filter is redundant but
        // harmless and makes the intent explicit.
        cmd.CommandText = """
            SELECT novarad_report_ids
            FROM billing.reconciliation_line_items
            WHERE tenant_id = @t
              AND run_id    = @run
              AND novarad_physician_id = @phys
              AND cpt_code  = @cpt
              AND site_code = @site
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("run", runId);
        cmd.Parameters.AddWithValue("phys", novaradPhysicianId);
        cmd.Parameters.AddWithValue("cpt", cptCode);
        cmd.Parameters.AddWithValue("site", siteCode);

        var raw = await cmd.ExecuteScalarAsync(cancellationToken);
        if (raw is null or DBNull) return null;
        return ((long[])raw).ToArray();
    }

    public async Task<ReconciliationRun?> GetRunWithLinesAsync(
        Guid tenantId,
        long runId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);

        // 1. Header — null when the run isn't visible to the tenant.
        ReconciliationRun? header;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT run_id, period_start, period_end, facility_id, run_kind,
                       total_reports, total_radiologists, total_work_rvu, notes::text,
                       generated_by_user_id, generated_at, stat_report_count
                FROM billing.reconciliation_runs
                WHERE tenant_id = @t AND run_id = @r
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("r", runId);
            await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await rdr.ReadAsync(cancellationToken)) return null;

            var notesJson = rdr.IsDBNull(8) ? "[]" : rdr.GetString(8);
            var notes = JsonSerializer.Deserialize<List<ReconciliationNote>>(notesJson)
                ?? new List<ReconciliationNote>();

            header = new ReconciliationRun(
                RunId: rdr.GetInt64(0),
                PeriodStart: new DateTimeOffset(rdr.GetDateTime(1), TimeSpan.Zero),
                PeriodEnd: new DateTimeOffset(rdr.GetDateTime(2), TimeSpan.Zero),
                FacilityId: rdr.IsDBNull(3) ? null : rdr.GetInt64(3),
                RunKind: rdr.GetInt16(4),
                TotalReports: rdr.GetInt32(5),
                TotalRadiologists: rdr.GetInt32(6),
                TotalWorkRvu: rdr.GetDecimal(7),
                StatReportCount: rdr.GetInt32(11),
                LineItems: Array.Empty<ReconciliationLineItem>(),
                Notes: notes,
                FacilitySummaries: Array.Empty<ReconciliationFacilitySummary>(),
                GeneratedByUserId: rdr.GetGuid(9),
                GeneratedAt: new DateTimeOffset(rdr.GetDateTime(10), TimeSpan.Zero));
        }

        // 2. Line items in display order (physician name, then CPT). Also collect
        //    the (site, facility, reports, stat-reports) tuples so the per-facility
        //    STAT subtotals can be rebuilt for the export without a separate table.
        var lines = new List<ReconciliationLineItem>();
        var rollupInputs = new List<(string SiteCode, long? FacilityId, IReadOnlyList<long> ReportIds, IReadOnlyList<long> StatReportIds)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT line_id, novarad_physician_id, physician_display_name,
                       site_code, facility_id, cpt_code, cpt_description,
                       report_count, units, work_rvu_per_unit, work_rvu_total,
                       novarad_rvu_work, rvu_mismatch, novarad_report_ids,
                       novarad_stat_report_ids
                FROM billing.reconciliation_line_items
                WHERE tenant_id = @t AND run_id = @r
                ORDER BY physician_display_name, cpt_code
                """;
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("r", runId);
            await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await rdr.ReadAsync(cancellationToken))
            {
                var siteCode = rdr.GetString(3);
                long? facilityId = rdr.IsDBNull(4) ? null : rdr.GetInt64(4);
                var reportIds = rdr.IsDBNull(13) ? Array.Empty<long>() : ((long[])rdr.GetValue(13)).ToArray();
                var statIds   = rdr.IsDBNull(14) ? Array.Empty<long>() : ((long[])rdr.GetValue(14)).ToArray();
                rollupInputs.Add((siteCode, facilityId, reportIds, statIds));
                lines.Add(new ReconciliationLineItem(
                    LineId: rdr.GetInt64(0),
                    NovaradPhysicianId: rdr.GetInt64(1),
                    PhysicianDisplayName: rdr.GetString(2),
                    SiteCode: siteCode,
                    FacilityId: facilityId,
                    CptCode: rdr.GetString(5),
                    CptDescription: rdr.IsDBNull(6) ? null : rdr.GetString(6),
                    ReportCount: rdr.GetInt32(7),
                    Units: rdr.GetDecimal(8),
                    WorkRvuPerUnit: rdr.GetDecimal(9),
                    WorkRvuTotal: rdr.GetDecimal(10),
                    NovaradRvuWork: rdr.IsDBNull(11) ? null : rdr.GetDecimal(11),
                    RvuMismatch: rdr.GetBoolean(12),
                    NovaradReportIds: reportIds,
                    NovaradStatReportIds: statIds));
            }
        }

        var (facilitySummaries, _) = BuildFacilityRollups(rollupInputs);
        return header with { LineItems = lines, FacilitySummaries = facilitySummaries };
    }

    private static async Task<MasterIndex> LoadMasterForYearsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId,
        short[] years, CancellationToken ct)
    {
        var singletons = new Dictionary<(short Year, string Code), CptCode>();
        // The bundle key is the sorted ;-joined uppercase CPT set. We compute it
        // once on load so the per-procedure lookup is O(1).
        var bundles = new Dictionary<(short Year, string SetKey), CptCode>();

        if (years.Length == 0) return new MasterIndex(singletons, bundles);

        // Base layer: cpt_codes (Amber's curated set + the bundles CMS won't carry).
        // Scoped in its own block so the reader closes before the overlay queries run on
        // this same connection — Npgsql permits only one active reader per connection.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT year, cpt_code, description, work_rvu, notes, is_active,
                       imported_from_import_id, created_at, updated_at
                FROM billing.cpt_codes
                WHERE tenant_id = @t
                  AND year = ANY(@years)
                  AND is_active = TRUE
                """;
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.Add(new NpgsqlParameter("years", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { Value = years });
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                var row = new CptCode(
                    Year: rdr.GetInt16(0),
                    Code: rdr.GetString(1),
                    Description: rdr.GetString(2),
                    WorkRvu: rdr.GetDecimal(3),
                    Notes: rdr.IsDBNull(4) ? null : rdr.GetString(4),
                    IsActive: rdr.GetBoolean(5),
                    ImportedFromImportId: rdr.IsDBNull(6) ? null : rdr.GetInt64(6),
                    CreatedAt: new DateTimeOffset(rdr.GetDateTime(7), TimeSpan.Zero),
                    UpdatedAt: new DateTimeOffset(rdr.GetDateTime(8), TimeSpan.Zero));

                if (row.Code.Contains(';'))
                {
                    var parts = row.Code.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    var key = NormalizeCptSetKey(parts);
                    bundles[(row.Year, key)] = row;
                }
                else
                {
                    singletons[(row.Year, NormalizeCpt(row.Code))] = row;
                }
            }
        }

        // ── RVU resolution precedence (item 1.2): rvu_overrides → rvu_values → cpt_codes.
        //    cpt_codes (above) is the base + the sole source of bundles. Now overlay the
        //    CMS per-HCPCS truth, then tenant-wide manual overrides on top.

        // Overlay CMS rvu_values: for a singleton already present, keep Amber's curated
        // description but take the CMS work RVU; codes CMS carries that Amber's sheet lacks
        // are ADDED so real CPTs on signed reports get credited from CMS truth. Gated to
        // status 'A' (active / separately payable) global (modifier='') rows with work_rvu>0,
        // so non-payable statuses (B bundled, N non-covered, I/X excluded, etc.) and 0-work
        // codes (category II/III) are NOT auto-credited and still surface on the unmapped
        // report — Amber can still curate those in cpt_codes or pin them via rvu_overrides.
        // DISTINCT ON picks the latest quarter when several are loaded for a year.
        await using (var cms = conn.CreateCommand())
        {
            cms.Transaction = tx;
            cms.CommandText = """
                SELECT DISTINCT ON (year, hcpcs) year, hcpcs, work_rvu, description
                FROM billing.rvu_values
                WHERE tenant_id = @t
                  AND year = ANY(@years)
                  AND modifier = ''
                  AND status_code = 'A'
                  AND work_rvu > 0
                ORDER BY year, hcpcs, quarter DESC
                """;
            cms.Parameters.AddWithValue("t", tenantId);
            cms.Parameters.Add(new NpgsqlParameter("years", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { Value = years });
            await using var crdr = await cms.ExecuteReaderAsync(ct);
            while (await crdr.ReadAsync(ct))
            {
                var y = crdr.GetInt16(0);
                var code = NormalizeCpt(crdr.GetString(1));
                var work = crdr.GetDecimal(2);
                var desc = crdr.IsDBNull(3) ? null : crdr.GetString(3);
                var key = (y, code);
                singletons[key] = singletons.TryGetValue(key, out var existing)
                    ? existing with { WorkRvu = work }                  // CMS RVU, keep curated description
                    : new CptCode(y, code, desc ?? code, work, Notes: "CMS PPRRVU",
                        IsActive: true, ImportedFromImportId: null,
                        CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow);
            }
        }

        // Top layer: tenant-wide manual RVU overrides win over CMS + cpt_codes. (Facility-
        // specific overrides aren't resolved here yet — reconciliation aggregates per site,
        // so per-facility override resolution is a later refinement.)
        await using (var ov = conn.CreateCommand())
        {
            ov.Transaction = tx;
            ov.CommandText = """
                SELECT year, cpt_code, override_work_rvu
                FROM billing.rvu_overrides
                WHERE tenant_id = @t
                  AND year = ANY(@years)
                  AND facility_id IS NULL
                """;
            ov.Parameters.AddWithValue("t", tenantId);
            ov.Parameters.Add(new NpgsqlParameter("years", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { Value = years });
            await using var ordr = await ov.ExecuteReaderAsync(ct);
            while (await ordr.ReadAsync(ct))
            {
                var y = ordr.GetInt16(0);
                var raw = ordr.GetString(1);
                var work = ordr.GetDecimal(2);
                if (raw.Contains(';'))
                {
                    // Bundle override: match the bundle dict by its normalized set-key
                    // (component order/case/dupes washed out) so a ;-delimited override
                    // actually lands. Previously the override only ever wrote `singletons`,
                    // so a bundle override silently did nothing.
                    var parts = raw.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    var setKey = NormalizeCptSetKey(parts);
                    var bkey = (y, setKey);
                    bundles[bkey] = bundles.TryGetValue(bkey, out var bexisting)
                        ? bexisting with { WorkRvu = work }
                        : new CptCode(y, raw, raw, work, Notes: "override",
                            IsActive: true, ImportedFromImportId: null,
                            CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow);
                }
                else
                {
                    var code = NormalizeCpt(raw);
                    var key = (y, code);
                    singletons[key] = singletons.TryGetValue(key, out var existing)
                        ? existing with { WorkRvu = work }
                        : new CptCode(y, code, code, work, Notes: "override",
                            IsActive: true, ImportedFromImportId: null,
                            CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow);
                }
            }
        }

        return new MasterIndex(singletons, bundles);
    }

    // Load the tenant's approved service_code → cpt_code mappings into a dict
    // for the matcher. Only status=1 (approved) rows participate in crediting;
    // status=2 (suppressed) rows are deliberately absent so those codes fall
    // through to the missing-code path and stay on the unmapped report.
    private static async Task<Dictionary<string, string>> LoadCrosswalkAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT service_code, cpt_code
            FROM billing.service_code_crosswalk
            WHERE tenant_id = @t AND status = 1
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
            map[NormalizeCpt(rdr.GetString(0))] = NormalizeCpt(rdr.GetString(1));
        return map;
    }

    private static async Task<Dictionary<string, long>> LoadFacilityMapAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, CancellationToken ct)
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT code, facility_id
            FROM tenancy.facilities
            WHERE tenant_id = @t AND is_active = TRUE
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
            map[rdr.GetString(0)] = rdr.GetInt32(1);  // facilities.facility_id is SERIAL (int4)
        return map;
    }

    // ------------------------------------------------------------------------
    // Matcher — pure in-memory; no DB.
    // ------------------------------------------------------------------------
    //
    // Per-procedure decision (the hypothesis from the handoff):
    //   1. Collect distinct CPTs on the procedure.
    //   2. If a bundle row's ;-split set equals that set → credit the bundle's
    //      RVU once for the procedure (units = 1).
    //   3. Otherwise → credit each CPT against its singleton master row at
    //      (sum of service-line units for that CPT) × master.work_rvu.
    //   4. CPTs missing from the master → surface as a note; do not credit.
    //
    // Then aggregate every emitted credit by (physician × site_code × master_code).
    //
    private static (List<AggregatedLine>, List<ReconciliationNote>, HashSet<string>) MatchAndAggregate(
        IReadOnlyList<SignedProcedureLineItem> source,
        MasterIndex master,
        IReadOnlyDictionary<string, string> crosswalk,
        Dictionary<string, long> facilityBySite)
    {
        var notes = new List<ReconciliationNote>();
        var emissions = new List<CreditEmission>();

        // Report-level STAT flag (ris.order_procedures.stat_flag, constant across a
        // report's service lines). Used to tag each aggregated line's contributing
        // reports so the per-facility STAT subtotal can de-duplicate by report.
        var statReportIds = new HashSet<long>();
        foreach (var s in source)
            if (s.IsStat) statReportIds.Add(s.ReportId);

        // Raw (normalized) service_codes that fired through the crosswalk this
        // run. Returned to the caller so reconciliation can bump applied_count /
        // last_used_at on the matching crosswalk rows.
        var appliedCrosswalkRawCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Codes seen on procedures that aren't in the master, accumulated by
        // (year, code) → distinct contributing reports. Emitted as one classified
        // note per code after the loop so the run surfaces the data-quality gap
        // instead of burying it in one note per occurrence.
        var missingByCode = new Dictionary<(short Year, string Code), HashSet<long>>();

        // Group source by procedure so we can see the full CPT set per procedure.
        var byProcedure = source.GroupBy(s => (s.ReportId, s.ProcedureId));
        foreach (var procGroup in byProcedure)
        {
            var rows = procGroup.ToList();
            var anyRow = rows[0];
            var year = (short)anyRow.SignedAt.Year;

            // Per-CPT units & Novarad RVU for this procedure (multiple service
            // lines with the same CPT collapse here). Crosswalk resolution runs
            // BEFORE this accumulation so resolved CPTs participate naturally in
            // the per-procedure setKey → bundle hits still work.
            var unitsByCpt = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            var novaradRvuByCpt = new Dictionary<string, decimal?>(StringComparer.OrdinalIgnoreCase);
            var viaCrosswalkCpts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                var rawCode = NormalizeCpt(row.CptCode);
                var (code, viaXwalk) = ResolveCode(row.CptCode, crosswalk);
                if (viaXwalk)
                {
                    appliedCrosswalkRawCodes.Add(rawCode);
                    viaCrosswalkCpts.Add(code);
                }
                unitsByCpt[code] = unitsByCpt.GetValueOrDefault(code) + row.Units;
                if (!novaradRvuByCpt.ContainsKey(code))
                    novaradRvuByCpt[code] = row.NovaradRvuWork;
            }

            var distinctCpts = unitsByCpt.Keys.ToArray();
            var setKey = NormalizeCptSetKey(distinctCpts);

            // Bundle attempt — only meaningful when the procedure carries 2+
            // CPTs (bundles are always multi-CPT joined by ';').
            if (distinctCpts.Length >= 2 && master.Bundles.TryGetValue((year, setKey), out var bundleRow))
            {
                emissions.Add(new CreditEmission(
                    NovaradPhysicianId: anyRow.SigningPhysicianId,
                    PhysicianDisplayName: anyRow.PhysicianDisplayName,
                    SiteCode: anyRow.SiteCode,
                    CptCode: bundleRow.Code,            // keep the bundle string verbatim
                    CptDescription: bundleRow.Description,
                    Units: 1m,
                    WorkRvuPerUnit: bundleRow.WorkRvu,
                    NovaradRvuWork: null,               // bundles aren't in Novarad's table
                    RvuMismatch: false,
                    ReportId: anyRow.ReportId));
                continue;
            }

            // Singleton fallback. Note in the run header when a CPT-set on a
            // multi-CPT procedure looked like it should match a bundle but
            // didn't — those are the "partial bundle" cases worth Amber's eyes.
            if (distinctCpts.Length >= 2)
            {
                notes.Add(new ReconciliationNote(
                    "no_bundle_match",
                    $"Procedure {anyRow.ProcedureId} (report {anyRow.ReportId}) has CPT set "
                    + $"[{string.Join(",", distinctCpts.OrderBy(c => c, StringComparer.Ordinal))}] "
                    + $"with no bundle row in year {year}; credited as singletons."));
            }

            foreach (var code in distinctCpts)
            {
                if (master.Singletons.TryGetValue((year, code), out var singletonRow))
                {
                    var units = unitsByCpt[code];
                    var novaradRvu = novaradRvuByCpt[code];
                    var viaXwalk = viaCrosswalkCpts.Contains(code);
                    // Comparing Novarad's local-code RVU against the resolved CPT's
                    // master RVU is meaningless when the credit came via crosswalk —
                    // the customer's code never claimed to be this CPT in the first place.
                    var mismatch = !viaXwalk && novaradRvu is not null && novaradRvu.Value != singletonRow.WorkRvu;
                    emissions.Add(new CreditEmission(
                        NovaradPhysicianId: anyRow.SigningPhysicianId,
                        PhysicianDisplayName: anyRow.PhysicianDisplayName,
                        SiteCode: anyRow.SiteCode,
                        CptCode: singletonRow.Code,
                        CptDescription: singletonRow.Description,
                        Units: units,
                        WorkRvuPerUnit: singletonRow.WorkRvu,
                        NovaradRvuWork: novaradRvu,
                        RvuMismatch: mismatch,
                        ReportId: anyRow.ReportId));
                }
                else
                {
                    var key = (year, code);
                    if (!missingByCode.TryGetValue(key, out var reports))
                        missingByCode[key] = reports = new HashSet<long>();
                    reports.Add(anyRow.ReportId);
                }
            }
        }

        // Emit one classified note per distinct missing code. The split tells the
        // reviewer which gaps are theirs to fix:
        //   cpt_missing_from_master — a real CPT absent from the RVU sheet → add it.
        //   non_cpt_service_code    — Novarad's service_code isn't a CPT at all →
        //                             map it to a CPT in Novarad (or supply a crosswalk).
        foreach (var (key, reports) in missingByCode.OrderBy(kv => kv.Key.Code, StringComparer.Ordinal))
        {
            var (year, code) = key;
            var (kind, action) = LooksLikeCpt(code)
                ? ("cpt_missing_from_master", "add it to the RVU master sheet")
                : ("non_cpt_service_code", "map it to a CPT in Novarad, or supply a code→CPT crosswalk");
            notes.Add(new ReconciliationNote(
                kind,
                $"{code} (year {year}) not in master; {reports.Count} report(s) uncredited. Action: {action}."));
        }

        // Aggregate emissions by (physician × site × cpt). Same cpt key implies
        // same master row, so work_rvu_per_unit is constant inside a group.
        var aggregated = emissions
            .GroupBy(e => (e.NovaradPhysicianId, e.SiteCode, e.CptCode))
            .Select(g =>
            {
                var rows = g.ToList();
                var first = rows[0];
                var unitsTotal = rows.Sum(r => r.Units);
                var rvuPerUnit = first.WorkRvuPerUnit;
                var rvuTotal   = unitsTotal * rvuPerUnit;
                var reportIds  = rows.Select(r => r.ReportId).Distinct().OrderBy(x => x).ToArray();
                var statIds    = reportIds.Where(statReportIds.Contains).ToArray();

                // If Novarad shows multiple distinct non-null RVUs for the same
                // singleton CPT within this run, that's a Novarad-side data drift —
                // surface it but pick the first observed value for the snapshot.
                var distinctNovaradRvus = rows
                    .Where(r => r.NovaradRvuWork is not null)
                    .Select(r => r.NovaradRvuWork!.Value)
                    .Distinct()
                    .ToArray();
                decimal? novaradRvu = distinctNovaradRvus.Length > 0 ? distinctNovaradRvus[0] : null;
                var mismatch = rows.Any(r => r.RvuMismatch) || distinctNovaradRvus.Length > 1;

                long? facilityId = facilityBySite.TryGetValue(first.SiteCode, out var fid) ? fid : null;

                return new AggregatedLine(
                    NovaradPhysicianId: first.NovaradPhysicianId,
                    PhysicianDisplayName: first.PhysicianDisplayName,
                    SiteCode: first.SiteCode,
                    FacilityId: facilityId,
                    CptCode: first.CptCode,
                    CptDescription: first.CptDescription,
                    ReportCount: reportIds.Length,
                    Units: unitsTotal,
                    WorkRvuPerUnit: rvuPerUnit,
                    WorkRvuTotal: rvuTotal,
                    NovaradRvuWork: novaradRvu,
                    RvuMismatch: mismatch,
                    ReportIds: reportIds,
                    StatReportIds: statIds);
            })
            .OrderBy(a => a.PhysicianDisplayName)
            .ThenBy(a => a.SiteCode)
            .ThenBy(a => a.CptCode, StringComparer.Ordinal)
            .ToList();

        return (aggregated, notes, appliedCrosswalkRawCodes);
    }

    private static string NormalizeCpt(string code) =>
        code.Trim().ToUpperInvariant();

    // Translate a raw Novarad service_code into the CPT the matcher should
    // credit, consulting the approved crosswalk. Returns (resolvedCode, viaCrosswalk)
    // so the caller can suppress noisy RvuMismatch flags (Novarad's local RVU vs.
    // the mapped CPT's master RVU is a meaningless comparison).
    private static (string ResolvedCode, bool ViaCrosswalk) ResolveCode(
        string rawNovaradCode, IReadOnlyDictionary<string, string> crosswalk)
    {
        var code = NormalizeCpt(rawNovaradCode);
        if (crosswalk.TryGetValue(code, out var target))
            return (NormalizeCpt(target), true);
        return (code, false);
    }

    // Heuristic: does this look like a real CPT/HCPCS code (vs an internal/
    // proprietary service code)? Catches Category I (5 digits), Category III
    // (4 digits + trailing letter, e.g. 0042T), and HCPCS Level II (letter +
    // 4 digits, e.g. J1885). Codes already arrive normalized (trimmed/upper).
    private static bool LooksLikeCpt(string code)
    {
        if (code.Length != 5) return false;
        bool d0 = char.IsAsciiDigit(code[0]), d1 = char.IsAsciiDigit(code[1]),
             d2 = char.IsAsciiDigit(code[2]), d3 = char.IsAsciiDigit(code[3]),
             d4 = char.IsAsciiDigit(code[4]);
        if (d0 && d1 && d2 && d3 && d4) return true;                 // Cat I
        if (d0 && d1 && d2 && d3 && char.IsAsciiLetterUpper(code[4])) return true;  // Cat III
        if (char.IsAsciiLetterUpper(code[0]) && d1 && d2 && d3 && d4) return true;  // HCPCS II
        return false;
    }

    private static string NormalizeCptSetKey(IEnumerable<string> codes)
    {
        var sorted = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var c in codes)
            sorted.Add(NormalizeCpt(c));
        return string.Join(";", sorted);
    }

    private sealed record MasterIndex(
        IReadOnlyDictionary<(short Year, string Code), CptCode> Singletons,
        IReadOnlyDictionary<(short Year, string SetKey), CptCode> Bundles);

    private sealed record CreditEmission(
        long NovaradPhysicianId,
        string PhysicianDisplayName,
        string SiteCode,
        string CptCode,
        string? CptDescription,
        decimal Units,
        decimal WorkRvuPerUnit,
        decimal? NovaradRvuWork,
        bool RvuMismatch,
        long ReportId);

    private sealed record AggregatedLine(
        long NovaradPhysicianId,
        string PhysicianDisplayName,
        string SiteCode,
        long? FacilityId,
        string CptCode,
        string? CptDescription,
        int ReportCount,
        decimal Units,
        decimal WorkRvuPerUnit,
        decimal WorkRvuTotal,
        decimal? NovaradRvuWork,
        bool RvuMismatch,
        IReadOnlyList<long> ReportIds,
        IReadOnlyList<long> StatReportIds);

    // Roll per-line (site, facility, reports, stat-reports) up to per-facility
    // subtotals + the run-level STAT count. Reports de-duplicate within a facility
    // (a report spanning multiple CPTs counts once) and across facilities for the
    // run total, so the subtotals reconcile to the run total.
    private static (IReadOnlyList<ReconciliationFacilitySummary> Summaries, int StatReportCount) BuildFacilityRollups(
        IEnumerable<(string SiteCode, long? FacilityId, IReadOnlyList<long> ReportIds, IReadOnlyList<long> StatReportIds)> lines)
    {
        var bySite = new Dictionary<string, (long? FacilityId, HashSet<long> Reports, HashSet<long> Stat)>(
            StringComparer.OrdinalIgnoreCase);
        var allStat = new HashSet<long>();

        foreach (var (site, facilityId, reportIds, statIds) in lines)
        {
            if (!bySite.TryGetValue(site, out var entry))
                entry = (facilityId, new HashSet<long>(), new HashSet<long>());
            else if (entry.FacilityId is null && facilityId is not null)
                entry = (facilityId, entry.Reports, entry.Stat);   // backfill facility_id if a later line resolved it

            foreach (var rid in reportIds) entry.Reports.Add(rid);
            foreach (var rid in statIds) { entry.Stat.Add(rid); allStat.Add(rid); }
            bySite[site] = entry;
        }

        var summaries = bySite
            .Select(kv => new ReconciliationFacilitySummary(
                FacilityId:      kv.Value.FacilityId,
                SiteCode:        kv.Key,
                TotalReports:    kv.Value.Reports.Count,
                StatReportCount: kv.Value.Stat.Count))
            .OrderByDescending(f => f.StatReportCount)
            .ThenByDescending(f => f.TotalReports)
            .ThenBy(f => f.SiteCode, StringComparer.Ordinal)
            .ToList();

        return (summaries, allStat.Count);
    }
}
