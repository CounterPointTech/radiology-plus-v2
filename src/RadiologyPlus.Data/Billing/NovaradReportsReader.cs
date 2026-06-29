using Npgsql;
using NpgsqlTypes;
using RadiologyPlus.Core.Billing;
using RadiologyPlus.Core.Data;

namespace RadiologyPlus.Data.Billing;

/// <summary>
/// Read-only access into a tenant's Novarad RIS for billing reconciliation.
/// Schema anchored to the live clone at 192.168.0.200; see
/// .claude/state/reconciliation-read-model.md for the join chain.
/// </summary>
public sealed class NovaradReportsReader : INovaradReportsReader
{
    private readonly INovaradDbContext _novarad;

    public NovaradReportsReader(INovaradDbContext novarad) => _novarad = novarad;

    // Novarad's signed_date columns are `timestamp without time zone` holding LOCAL wall-clock.
    // For window params: convert the bound to local wall-clock with Kind=Unspecified so Npgsql
    // sends `timestamp without time zone` and the DB does a literal (timezone-free) comparison.
    private static DateTime LocalNaive(DateTimeOffset bound) =>
        DateTime.SpecifyKind(bound.ToLocalTime().DateTime, DateTimeKind.Unspecified);

    // For read values: tag the local wall-clock with the machine's local offset so it carries
    // the correct instant (and same-tz displays show the original wall-clock).
    private static DateTimeOffset LocalWallClock(DateTime novaradLocal) =>
        new(DateTime.SpecifyKind(novaradLocal, DateTimeKind.Local));

    public async Task<IReadOnlyList<SignedReportSummary>> ReadSignedReportCountsAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        string? siteCode,
        CancellationToken cancellationToken = default)
    {
        if (windowEnd <= windowStart)
            throw new ArgumentException("windowEnd must be strictly after windowStart.", nameof(windowEnd));

        await using var conn = (NpgsqlConnection)await _novarad.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();

        var siteFilter = string.IsNullOrWhiteSpace(siteCode) ? "" : "AND o.site_code = @site";
        cmd.CommandText = $"""
            SELECT
                r.signing_physician_id,
                COALESCE(NULLIF(TRIM(COALESCE(pe.first_name, '') || ' ' || COALESCE(pe.last_name, '')), ''),
                         'Physician #' || r.signing_physician_id::text) AS physician_name,
                o.site_code,
                COUNT(*)::int AS signed_count,
                MIN(r.signed_date) AS earliest,
                MAX(r.signed_date) AS latest
            FROM ris.reports r
            JOIN ris.order_procedures op ON op.procedure_id = r.procedure_id
            JOIN ris.orders o            ON o.order_id      = op.order_id
            LEFT JOIN ris.physicians ph  ON ph.physician_id = r.signing_physician_id
            LEFT JOIN ris.people pe      ON pe.person_id    = ph.person_id
            WHERE r.signed_date IS NOT NULL
              AND r.status IN ('Signed', 'Finalized')
              AND r.signed_date >= @window_start
              AND r.signed_date <  @window_end
              AND r.signing_physician_id IS NOT NULL
              {siteFilter}
            GROUP BY r.signing_physician_id, physician_name, o.site_code
            ORDER BY signed_count DESC, physician_name
            """;
        // Novarad's signed_date is `timestamp without time zone` holding LOCAL wall-clock.
        // Bind the window bounds as naive local wall-clock (Kind=Unspecified → Npgsql sends
        // `timestamp without time zone`) so the comparison is a pure literal match, not a
        // session-timezone-dependent coercion.
        cmd.Parameters.AddWithValue("window_start", LocalNaive(windowStart));
        cmd.Parameters.AddWithValue("window_end", LocalNaive(windowEnd));
        if (!string.IsNullOrWhiteSpace(siteCode))
            cmd.Parameters.AddWithValue("site", siteCode);

        var result = new List<SignedReportSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new SignedReportSummary(
                SigningPhysicianId:    reader.GetInt64(0),
                PhysicianDisplayName:  reader.GetString(1),
                SiteCode:              reader.GetString(2),
                SignedReportCount:     reader.GetInt32(3),
                EarliestSignedAt:      LocalWallClock(reader.GetDateTime(4)),
                LatestSignedAt:        LocalWallClock(reader.GetDateTime(5))));
        }
        return result;
    }

    public async Task<IReadOnlyList<string>> ReadAllSiteCodesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _novarad.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        // Every site the customer actually books orders at (the ~166), the universe the
        // crosswalk facility-scoping picker offers. shared.sites also exists but carries
        // configured-but-unused sites; ris.orders is the billing-relevant set.
        cmd.CommandText = """
            SELECT DISTINCT site_code
            FROM ris.orders
            WHERE site_code IS NOT NULL AND TRIM(site_code) <> ''
            ORDER BY site_code
            """;
        var result = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(reader.GetString(0));
        return result;
    }

    public async Task<IReadOnlyList<SignedProcedureLineItem>> ReadSignedProcedureLineItemsAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        string? siteCode,
        CancellationToken cancellationToken = default)
    {
        if (windowEnd <= windowStart)
            throw new ArgumentException("windowEnd must be strictly after windowStart.", nameof(windowEnd));

        await using var conn = (NpgsqlConnection)await _novarad.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();

        var siteFilter = string.IsNullOrWhiteSpace(siteCode) ? "" : "AND o.site_code = @site";
        // No GROUP BY — we return at service-line grain so the matcher can collapse
        // by (report_id, procedure_id). Service lines with NULL service_code_id are
        // unbillable in Novarad's own ledger, so we skip them on the read side.
        cmd.CommandText = $"""
            SELECT
                r.report_id,
                op.procedure_id,
                r.signing_physician_id,
                COALESCE(NULLIF(TRIM(COALESCE(pe.first_name, '') || ' ' || COALESCE(pe.last_name, '')), ''),
                         'Physician #' || r.signing_physician_id::text) AS physician_name,
                o.site_code,
                r.signed_date,
                bsc.service_code AS cpt_code,
                COALESCE(sl.units, 1)::numeric(10,2) AS units,
                -- bsc.description is NULL for ~93% of this customer's service codes
                -- (incl. every unmapped one), so fall back to the procedure's exam
                -- name (op.procedure_name is fully populated) to give a human a
                -- meaningful label for mapping. Only the unmapped report consumes
                -- this — credited reconciliation lines show the CPT-master description.
                COALESCE(NULLIF(TRIM(bsc.description::text), ''),
                         NULLIF(TRIM(op.procedure_name::text), '')) AS cpt_description,
                bsc.rvu_work AS novarad_rvu_work,
                op.stat_flag AS stat_flag
            FROM ris.reports r
            JOIN ris.order_procedures op              ON op.procedure_id = r.procedure_id
            JOIN ris.orders o                         ON o.order_id      = op.order_id
            JOIN ris.order_procedure_service_lines sl ON sl.procedure_id = op.procedure_id
            JOIN ris.billing_service_codes bsc        ON bsc.service_code_id = sl.service_code_id
            LEFT JOIN ris.physicians ph               ON ph.physician_id = r.signing_physician_id
            LEFT JOIN ris.people pe                   ON pe.person_id    = ph.person_id
            WHERE r.signed_date IS NOT NULL
              AND r.status IN ('Signed', 'Finalized')
              AND r.signed_date >= @window_start
              AND r.signed_date <  @window_end
              AND r.signing_physician_id IS NOT NULL
              AND sl.service_code_id IS NOT NULL
              {siteFilter}
            ORDER BY r.signing_physician_id, r.report_id, op.procedure_id, bsc.service_code
            """;
        cmd.Parameters.AddWithValue("window_start", LocalNaive(windowStart));
        cmd.Parameters.AddWithValue("window_end", LocalNaive(windowEnd));
        if (!string.IsNullOrWhiteSpace(siteCode))
            cmd.Parameters.AddWithValue("site", siteCode);

        var result = new List<SignedProcedureLineItem>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new SignedProcedureLineItem(
                ReportId:             reader.GetInt64(0),
                ProcedureId:          reader.GetInt64(1),
                SigningPhysicianId:   reader.GetInt64(2),
                PhysicianDisplayName: reader.GetString(3),
                SiteCode:             reader.GetString(4),
                SignedAt:             LocalWallClock(reader.GetDateTime(5)),
                CptCode:              reader.GetString(6),
                Units:                reader.GetDecimal(7),
                CptDescription:       reader.IsDBNull(8) ? null : reader.GetString(8),
                NovaradRvuWork:       reader.IsDBNull(9) ? null : reader.GetDecimal(9),
                IsStat:               !reader.IsDBNull(10) && reader.GetBoolean(10)));
        }
        return result;
    }

    public async Task<IReadOnlyList<ReconciliationDetailRow>> ReadReportDetailsAsync(
        IReadOnlyList<long> reportIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reportIds);
        if (reportIds.Count == 0)
            return Array.Empty<ReconciliationDetailRow>();

        await using var conn = (NpgsqlConnection)await _novarad.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();

        // ris.orders.accession_number ↔ pacs.studies.accession (citext); pacs.studies →
        // pacs.patients via the FK on pacs.studies.patient. LEFT JOIN on the PACS side
        // because not every order has a study row (e.g. ordered-not-yet-imaged), and
        // soft-deleted study rows are filtered out. A duplicate accession in pacs.studies
        // would fan out the result; we DISTINCT ON (report_id) to keep one row per report
        // — picking the most recent valid study as the representative.
        cmd.CommandText = """
            SELECT DISTINCT ON (r.report_id)
                r.report_id,
                r.signed_date,
                o.order_id,
                o.site_code::text,
                o.accession_number::text,
                s.study_uid::text,
                s.study_date,
                s.modality::text,
                p.id AS patient_row_id,
                p.patient_id::text,
                p.last_name::text,
                p.first_name::text,
                p.birth_time,
                p.gender::text
            FROM ris.reports r
            JOIN ris.order_procedures op ON op.procedure_id = r.procedure_id
            JOIN ris.orders o            ON o.order_id      = op.order_id
            LEFT JOIN pacs.studies s     ON s.accession     = o.accession_number AND s.is_valid = TRUE
            LEFT JOIN pacs.patients p    ON p.id            = s.patient
            WHERE r.report_id = ANY(@ids)
            ORDER BY r.report_id, s.study_date DESC NULLS LAST
            """;
        cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
        {
            Value = reportIds.ToArray(),
        });

        var result = new List<ReconciliationDetailRow>(reportIds.Count);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ReconciliationDetailRow(
                ReportId:           reader.GetInt64(0),
                SignedAt:           LocalWallClock(reader.GetDateTime(1)),
                OrderId:            reader.GetInt64(2),
                SiteCode:           reader.GetString(3),
                Accession:          reader.IsDBNull(4) ? null : reader.GetString(4),
                StudyUid:           reader.IsDBNull(5) ? null : reader.GetString(5),
                StudyDate:          reader.IsDBNull(6) ? null : LocalWallClock(reader.GetDateTime(6)),
                Modality:           reader.IsDBNull(7) ? null : reader.GetString(7),
                NovaradPatientId:   reader.IsDBNull(8) ? null : reader.GetInt64(8),
                PatientPid:         reader.IsDBNull(9) ? null : reader.GetString(9),
                PatientLastName:    reader.IsDBNull(10) ? null : reader.GetString(10),
                PatientFirstName:   reader.IsDBNull(11) ? null : reader.GetString(11),
                PatientBirthDate:   reader.IsDBNull(12) ? null : DateOnly.FromDateTime(reader.GetDateTime(12)),
                PatientGender:      reader.IsDBNull(13) ? null : reader.GetString(13)));
        }
        return result;
    }

    public async Task<ReportContent?> ReadReportFullAsync(
        long reportId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _novarad.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        // Same accession-driven join chain as ReadReportDetailsAsync, augmented with
        // the report body, format, and signing-physician name. DISTINCT ON (report_id)
        // collapses duplicate-accession fan-out down to one row per report.
        cmd.CommandText = """
            SELECT DISTINCT ON (r.report_id)
                r.report_id,
                r.signed_date,
                r.signing_physician_id,
                NULLIF(TRIM(COALESCE(pe.first_name, '') || ' ' || COALESCE(pe.last_name, '')), '') AS signing_physician_name,
                o.accession_number::text,
                s.study_date,
                s.modality::text,
                p.id AS patient_row_id,
                p.patient_id::text,
                p.last_name::text,
                p.first_name::text,
                p.birth_time,
                p.gender::text,
                r.report_format::text,
                r.report_text::text
            FROM ris.reports r
            JOIN ris.order_procedures op ON op.procedure_id = r.procedure_id
            JOIN ris.orders o            ON o.order_id      = op.order_id
            LEFT JOIN ris.physicians ph  ON ph.physician_id = r.signing_physician_id
            LEFT JOIN ris.people pe      ON pe.person_id    = ph.person_id
            LEFT JOIN pacs.studies s     ON s.accession     = o.accession_number AND s.is_valid = TRUE
            LEFT JOIN pacs.patients p    ON p.id            = s.patient
            WHERE r.report_id = @id
            ORDER BY r.report_id, s.study_date DESC NULLS LAST
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("id", reportId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new ReportContent(
            ReportId:             reader.GetInt64(0),
            SignedAt:             reader.IsDBNull(1) ? null : LocalWallClock(reader.GetDateTime(1)),
            SigningPhysicianId:   reader.IsDBNull(2) ? null : reader.GetInt64(2),
            SigningPhysicianName: reader.IsDBNull(3) ? null : reader.GetString(3),
            Accession:            reader.IsDBNull(4) ? null : reader.GetString(4),
            StudyDate:            reader.IsDBNull(5) ? null : LocalWallClock(reader.GetDateTime(5)),
            Modality:             reader.IsDBNull(6) ? null : reader.GetString(6),
            NovaradPatientId:     reader.IsDBNull(7) ? null : reader.GetInt64(7),
            PatientPid:           reader.IsDBNull(8) ? null : reader.GetString(8),
            PatientLastName:      reader.IsDBNull(9) ? null : reader.GetString(9),
            PatientFirstName:     reader.IsDBNull(10) ? null : reader.GetString(10),
            PatientBirthDate:     reader.IsDBNull(11) ? null : DateOnly.FromDateTime(reader.GetDateTime(11)),
            PatientGender:        reader.IsDBNull(12) ? null : reader.GetString(12),
            ReportFormat:         reader.IsDBNull(13) ? null : reader.GetString(13),
            ReportText:           reader.IsDBNull(14) ? null : reader.GetString(14));
    }
}
