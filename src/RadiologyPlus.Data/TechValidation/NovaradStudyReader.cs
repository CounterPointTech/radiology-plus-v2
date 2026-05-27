using Npgsql;
using NpgsqlTypes;
using RadiologyPlus.Core.Data;
using RadiologyPlus.Core.TechValidation;

namespace RadiologyPlus.Data.TechValidation;

/// <summary>
/// Read-only access into a tenant's Novarad. Queries are anchored to the actual
/// schema observed on the 192.168.0.200 clone (see docs/novarad-password-algorithm.md
/// for the wider knowledge dump).
/// </summary>
public sealed class NovaradStudyReader : INovaradStudyReader
{
    private readonly INovaradDbContext _novarad;

    public NovaradStudyReader(INovaradDbContext novarad) => _novarad = novarad;

    public async Task<IReadOnlyList<ReadyStudy>> ReadReadyStudiesAsync(
        TimeSpan window,
        CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _novarad.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        // Worklist filter:
        //   s.status = 0 (NEW per pacs.StudyStatus enum: 0=New, 1=Reviewed, ..., 5=Locked)
        //   AND last_image_processed_date within the window
        // We deliberately do NOT filter on is_valid — the tech is about to set is_valid=TRUE
        // via Do-the-Do, so excluding is_valid=FALSE rows would defeat the purpose of the
        // worklist. custom_3 stays in the projection as diagnostic context but is no longer
        // the load-bearing signal.
        cmd.CommandText = """
            SELECT
                s.id, s.study_uid::text, s.accession::text, s.study_date, s.modality::text,
                s.custom_3::text, s.facility_id, s.last_image_processed_date,
                p.id AS patient_row_id, p.patient_id::text, p.last_name::text, p.first_name::text, p.birth_time, p.gender::text
            FROM pacs.studies s
            JOIN pacs.patients p ON p.id = s.patient
            WHERE s.status = 0
              AND s.last_image_processed_date IS NOT NULL
              AND s.last_image_processed_date > LOCALTIMESTAMP - @window
            ORDER BY s.last_image_processed_date DESC
            LIMIT 500
            """;
        cmd.Parameters.Add(new NpgsqlParameter("window", NpgsqlDbType.Interval) { Value = window });

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var list = new List<ReadyStudy>();
        while (await reader.ReadAsync(cancellationToken))
            list.Add(MapReadyStudy(reader));
        return list;
    }

    public async Task<ReadyStudy?> ReadStudyByIdAsync(
        long novaradStudyId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _novarad.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        // Same projection shape as ReadReadyStudiesAsync but for one study and with no
        // ready/window filter — we want the study's current state (e.g. right after a
        // patient correction or reassignment) regardless of whether it still qualifies.
        cmd.CommandText = """
            SELECT
                s.id, s.study_uid::text, s.accession::text, s.study_date, s.modality::text,
                s.custom_3::text, s.facility_id, s.last_image_processed_date,
                p.id AS patient_row_id, p.patient_id::text, p.last_name::text, p.first_name::text, p.birth_time, p.gender::text
            FROM pacs.studies s
            JOIN pacs.patients p ON p.id = s.patient
            WHERE s.id = @id
            """;
        cmd.Parameters.AddWithValue("id", novaradStudyId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapReadyStudy(reader) : null;
    }

    public async Task<IReadOnlyList<PatientSearchResult>> SearchPatientsAsync(
        string query, int limit, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _novarad.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        // Match on PID (prefix) or name (contains), case-insensitive. Most-recently-active first.
        cmd.CommandText = """
            SELECT p.id, p.patient_id::text, p.last_name::text, p.first_name::text, p.middle_name::text,
                   p.birth_time, p.gender::text, p.recent_study_date
            FROM pacs.patients p
            WHERE p.patient_id::text ILIKE @prefix
               OR p.last_name::text ILIKE @contains
               OR p.first_name::text ILIKE @contains
            ORDER BY p.recent_study_date DESC NULLS LAST, p.last_name::text
            LIMIT @lim
            """;
        cmd.Parameters.Add(new NpgsqlParameter("prefix", NpgsqlDbType.Text) { Value = query + "%" });
        cmd.Parameters.Add(new NpgsqlParameter("contains", NpgsqlDbType.Text) { Value = "%" + query + "%" });
        cmd.Parameters.AddWithValue("lim", limit);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var list = new List<PatientSearchResult>();
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new PatientSearchResult(
                NovaradPatientId: reader.GetInt64(0),
                Pid: reader.IsDBNull(1) ? null : reader.GetString(1),
                LastName: reader.IsDBNull(2) ? null : reader.GetString(2),
                FirstName: reader.IsDBNull(3) ? null : reader.GetString(3),
                MiddleName: reader.IsDBNull(4) ? null : reader.GetString(4),
                BirthDate: reader.IsDBNull(5) ? null : DateOnly.FromDateTime(reader.GetDateTime(5)),
                Gender: reader.IsDBNull(6) ? null : reader.GetString(6),
                RecentStudyDate: reader.IsDBNull(7) ? null : LocalWallClock(reader.GetDateTime(7))));
        }
        return list;
    }

    // Novarad stores timestamps as `timestamp without time zone` holding LOCAL wall-clock.
    // Tag the read value with the machine's local offset so it round-trips as the correct
    // instant through our timestamptz projection (and same-tz displays show the wall-clock).
    private static DateTimeOffset LocalWallClock(DateTime novaradLocal) =>
        new(DateTime.SpecifyKind(novaradLocal, DateTimeKind.Local));

    private static ReadyStudy MapReadyStudy(NpgsqlDataReader reader) =>
        new(
            NovaradStudyId: reader.GetInt64(0),
            FacilityId: reader.GetInt32(6),
            StudyUid: reader.GetString(1),
            Accession: reader.IsDBNull(2) ? null : reader.GetString(2),
            StudyDate: reader.IsDBNull(3) ? null : LocalWallClock(reader.GetDateTime(3)),
            Modality: reader.IsDBNull(4) ? null : reader.GetString(4),
            Custom3: reader.IsDBNull(5) ? null : reader.GetString(5),
            NovaradPatientId: reader.GetInt64(8),
            PatientPid: reader.IsDBNull(9) ? null : reader.GetString(9),
            PatientLastName: reader.IsDBNull(10) ? null : reader.GetString(10),
            PatientFirstName: reader.IsDBNull(11) ? null : reader.GetString(11),
            PatientBirthDate: reader.IsDBNull(12) ? null : DateOnly.FromDateTime(reader.GetDateTime(12)),
            PatientGender: reader.IsDBNull(13) ? null : reader.GetString(13),
            LastImageProcessedDate: reader.IsDBNull(7) ? null : LocalWallClock(reader.GetDateTime(7)),
            ProjectedAt: DateTimeOffset.UtcNow,
            InProgressValidationId: null,
            InProgressStatus: null,
            InProgressStartedBy: null,
            InProgressStartedByDisplay: null,
            InProgressStartedAt: null);

    public async Task<IReadOnlyList<CandidateOrder>> ReadCandidateOrdersForPatientAsync(
        string patientPid, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _novarad.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT order_id, patient_id::text, accession_number::text, status::text,
                   description::text, physician_reason::text, notes::text,
                   referring_physician_id, creation_date
            FROM ris.orders
            WHERE patient_id = @pid
            ORDER BY creation_date DESC NULLS LAST
            LIMIT 100
            """;
        cmd.Parameters.Add(new NpgsqlParameter("pid", patientPid));

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var list = new List<CandidateOrder>();
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new CandidateOrder(
                OrderId: reader.GetInt64(0),
                PatientId: reader.GetString(1),
                AccessionNumber: reader.GetString(2),
                Status: reader.GetString(3),
                Description: reader.IsDBNull(4) ? null : reader.GetString(4),
                PhysicianReason: reader.IsDBNull(5) ? null : reader.GetString(5),
                Notes: reader.IsDBNull(6) ? null : reader.GetString(6),
                ReferringPhysicianId: reader.IsDBNull(7) ? null : reader.GetInt64(7),
                CreationDate: reader.IsDBNull(8) ? null : LocalWallClock(reader.GetDateTime(8))));
        }
        return list;
    }

    public async Task<IReadOnlyList<ComparisonCandidate>> ReadComparisonCandidatesAsync(
        long novaradPatientId, string? lastNameFuzzy, int limit, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _novarad.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        // Comparison candidates: studies for the same patient_row_id, OR studies whose patient
        // has the same last_name (case-insensitive) when a fuzzy hint is supplied.
        cmd.CommandText = """
            SELECT s.id, s.study_uid::text, s.accession::text, s.study_date, s.modality::text,
                   p.id AS patient_row_id, p.last_name::text, p.first_name::text, p.birth_time
            FROM pacs.studies s
            JOIN pacs.patients p ON p.id = s.patient
            WHERE (s.patient = @pid OR (@fuzzy IS NOT NULL AND p.last_name = @fuzzy))
              AND s.is_valid = TRUE
            ORDER BY s.study_date DESC NULLS LAST
            LIMIT @lim
            """;
        cmd.Parameters.AddWithValue("pid", novaradPatientId);
        cmd.Parameters.Add(new NpgsqlParameter("fuzzy", NpgsqlDbType.Text) { Value = (object?)lastNameFuzzy ?? DBNull.Value });
        cmd.Parameters.AddWithValue("lim", limit);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var list = new List<ComparisonCandidate>();
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new ComparisonCandidate(
                NovaradStudyId: reader.GetInt64(0),
                StudyUid: reader.GetString(1),
                Accession: reader.IsDBNull(2) ? null : reader.GetString(2),
                StudyDate: reader.IsDBNull(3) ? null : LocalWallClock(reader.GetDateTime(3)),
                Modality: reader.IsDBNull(4) ? null : reader.GetString(4),
                NovaradPatientId: reader.GetInt64(5),
                PatientLastName: reader.IsDBNull(6) ? null : reader.GetString(6),
                PatientFirstName: reader.IsDBNull(7) ? null : reader.GetString(7),
                PatientBirthDate: reader.IsDBNull(8) ? null : DateOnly.FromDateTime(reader.GetDateTime(8))));
        }
        return list;
    }
}
