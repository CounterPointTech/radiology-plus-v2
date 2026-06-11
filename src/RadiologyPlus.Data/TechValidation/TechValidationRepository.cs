using Npgsql;
using NpgsqlTypes;
using RadiologyPlus.Core.Data;
using RadiologyPlus.Core.TechValidation;

namespace RadiologyPlus.Data.TechValidation;

public sealed class TechValidationRepository : ITechValidationRepository
{
    private readonly IAppDbContext _db;

    public TechValidationRepository(IAppDbContext db) => _db = db;

    public async Task<IReadOnlyList<ReadyStudy>> GetWorklistAsync(
        Guid tenantId,
        IReadOnlyCollection<int>? facilityIdFilter,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();

        var filterClause = facilityIdFilter is { Count: > 0 } ? " AND facility_id = ANY(@facs)" : "";
        // LEFT JOIN identity.users so the UI can render a display name on the
        // "In progress by <user>" badge instead of a raw Guid. identity.users.user_id
        // is globally unique so no tenant predicate is needed on the join.
        cmd.CommandText = $"""
            SELECT w.novarad_study_id, w.facility_id, w.study_uid, w.accession, w.study_date, w.modality, w.custom_3,
                   w.novarad_patient_id, w.patient_pid, w.patient_last_name, w.patient_first_name, w.patient_birth_date,
                   w.last_image_processed_date, w.projected_at,
                   w.in_progress_validation_id, w.in_progress_status, w.in_progress_started_by,
                   COALESCE(u.display_name, u.username) AS in_progress_started_by_display,
                   w.in_progress_started_at, w.patient_gender, w.study_description
            FROM tech_validation.vw_worklist w
            LEFT JOIN identity.users u ON u.user_id = w.in_progress_started_by
            WHERE w.tenant_id = @t {filterClause.Replace("facility_id", "w.facility_id")}
            ORDER BY COALESCE(w.last_image_processed_date, w.projected_at) DESC, w.novarad_study_id DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("limit", limit);
        if (facilityIdFilter is { Count: > 0 })
            cmd.Parameters.Add(new NpgsqlParameter("facs", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = facilityIdFilter.ToArray() });

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var list = new List<ReadyStudy>();
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new ReadyStudy(
                NovaradStudyId: reader.GetInt64(0),
                FacilityId: reader.GetInt32(1),
                StudyUid: reader.GetString(2),
                Accession: reader.IsDBNull(3) ? null : reader.GetString(3),
                StudyDate: reader.IsDBNull(4) ? null : new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero),
                Modality: reader.IsDBNull(5) ? null : reader.GetString(5),
                Custom3: reader.IsDBNull(6) ? null : reader.GetString(6),
                StudyDescription: reader.IsDBNull(20) ? null : reader.GetString(20),
                NovaradPatientId: reader.GetInt64(7),
                PatientPid: reader.IsDBNull(8) ? null : reader.GetString(8),
                PatientLastName: reader.IsDBNull(9) ? null : reader.GetString(9),
                PatientFirstName: reader.IsDBNull(10) ? null : reader.GetString(10),
                PatientBirthDate: reader.IsDBNull(11) ? null : DateOnly.FromDateTime(reader.GetDateTime(11)),
                PatientGender: reader.IsDBNull(19) ? null : reader.GetString(19),
                LastImageProcessedDate: reader.IsDBNull(12) ? null : new DateTimeOffset(reader.GetDateTime(12), TimeSpan.Zero),
                ProjectedAt: new DateTimeOffset(reader.GetDateTime(13), TimeSpan.Zero),
                InProgressValidationId: reader.IsDBNull(14) ? null : reader.GetGuid(14),
                InProgressStatus: reader.IsDBNull(15) ? null : (ValidationStatus)reader.GetInt16(15),
                InProgressStartedBy: reader.IsDBNull(16) ? null : reader.GetGuid(16),
                InProgressStartedByDisplay: reader.IsDBNull(17) ? null : reader.GetString(17),
                InProgressStartedAt: reader.IsDBNull(18) ? null : new DateTimeOffset(reader.GetDateTime(18), TimeSpan.Zero)));
        }
        return list;
    }

    public async Task<Guid> StartValidationAsync(
        Guid tenantId, long novaradStudyId, long novaradPatientId, Guid startedByUserId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tech_validation.validations
                (tenant_id, novarad_study_id, novarad_patient_id, current_step, status, started_by_user_id)
            VALUES (@t, @s, @p, 1, 2, @u)
            RETURNING validation_id
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("s", novaradStudyId);
        cmd.Parameters.AddWithValue("p", novaradPatientId);
        cmd.Parameters.AddWithValue("u", startedByUserId);
        return (Guid)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<ValidationRecord?> GetValidationAsync(
        Guid tenantId, Guid validationId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectValidationSql + " WHERE validation_id = @v";
        cmd.Parameters.AddWithValue("v", validationId);
        return await ReadOneValidationAsync(cmd, cancellationToken);
    }

    public async Task<ValidationRecord?> GetOpenValidationForStudyAsync(
        Guid tenantId, long novaradStudyId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectValidationSql + " WHERE novarad_study_id = @s AND status IN (1, 2) LIMIT 1";
        cmd.Parameters.AddWithValue("s", novaradStudyId);
        return await ReadOneValidationAsync(cmd, cancellationToken);
    }

    public async Task UpdateStepAsync(
        Guid tenantId, Guid validationId, WizardStep wizardStep, ValidationStepUpdate update,
        CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        // The patient-action block is written as a unit, gated on @paction_provided
        // (= the caller set PatientAction). That lets step 1 overwrite or clear the
        // whole block while later steps, which pass PatientAction=null, leave it alone.
        cmd.CommandText = """
            UPDATE tech_validation.validations SET
                current_step                   = GREATEST(current_step, @step),
                novarad_order_id               = COALESCE(@order, novarad_order_id),
                novarad_patient_id             = COALESCE(@patient, novarad_patient_id),
                reason                         = COALESCE(@reason, reason),
                tech_notes                     = COALESCE(@notes, tech_notes),
                referring_physician_id         = COALESCE(@refphys, referring_physician_id),
                comparison_study_ids           = COALESCE(@comparisons, comparison_study_ids),
                create_order_requested         = COALESCE(@createorder, create_order_requested),
                patient_action                 = CASE WHEN @paction_provided THEN @paction ELSE patient_action END,
                corrected_last_name            = CASE WHEN @paction_provided THEN @clast ELSE corrected_last_name END,
                corrected_first_name           = CASE WHEN @paction_provided THEN @cfirst ELSE corrected_first_name END,
                corrected_middle_name          = CASE WHEN @paction_provided THEN @cmiddle ELSE corrected_middle_name END,
                corrected_birth_date           = CASE WHEN @paction_provided THEN @cbirth ELSE corrected_birth_date END,
                corrected_gender               = CASE WHEN @paction_provided THEN @cgender ELSE corrected_gender END,
                reassign_target_patient_id     = CASE WHEN @paction_provided THEN @rtarget ELSE reassign_target_patient_id END
            WHERE validation_id = @v
            """;
        cmd.Parameters.AddWithValue("v", validationId);
        cmd.Parameters.AddWithValue("step", (short)wizardStep);
        cmd.Parameters.Add(NullableBigInt("order", update.NovaradOrderId));
        cmd.Parameters.Add(NullableBigInt("patient", update.NovaradPatientId));
        cmd.Parameters.Add(NullableText("reason", update.Reason));
        cmd.Parameters.Add(NullableText("notes", update.TechNotes));
        cmd.Parameters.Add(NullableBigInt("refphys", update.ReferringPhysicianId));
        cmd.Parameters.Add(new NpgsqlParameter("comparisons", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
        {
            Value = (object?)update.ComparisonStudyIds ?? DBNull.Value
        });
        cmd.Parameters.Add(new NpgsqlParameter("createorder", NpgsqlDbType.Boolean)
        {
            Value = (object?)update.CreateOrderRequested ?? DBNull.Value
        });
        cmd.Parameters.AddWithValue("paction_provided", update.PatientAction.HasValue);
        cmd.Parameters.Add(new NpgsqlParameter("paction", NpgsqlDbType.Smallint)
        {
            Value = update.PatientAction.HasValue ? (short)update.PatientAction.Value : DBNull.Value
        });
        cmd.Parameters.Add(NullableText("clast", update.Correction?.LastName));
        cmd.Parameters.Add(NullableText("cfirst", update.Correction?.FirstName));
        cmd.Parameters.Add(NullableText("cmiddle", update.Correction?.MiddleName));
        cmd.Parameters.Add(NullableDate("cbirth", update.Correction?.BirthDate));
        cmd.Parameters.Add(NullableText("cgender", update.Correction?.Gender));
        cmd.Parameters.Add(NullableBigInt("rtarget", update.ReassignTargetPatientId));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetStatusAsync(
        Guid tenantId, Guid validationId, ValidationStatus status,
        CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE tech_validation.validations SET status = @s WHERE validation_id = @v";
        cmd.Parameters.AddWithValue("v", validationId);
        cmd.Parameters.AddWithValue("s", (short)status);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkCompletedAsync(
        Guid tenantId, Guid validationId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE tech_validation.validations
            SET status = 4, completed_at = NOW(), current_step = 6
            WHERE validation_id = @v
            """;
        cmd.Parameters.AddWithValue("v", validationId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long> RecordRunStartedAsync(
        Guid tenantId, Guid validationId, string stepKey, int stepOrder,
        CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tech_validation.do_the_do_runs (tenant_id, validation_id, step_key, step_order, status)
            VALUES (@t, @v, @k, @o, 1)
            RETURNING run_id
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("v", validationId);
        cmd.Parameters.AddWithValue("k", stepKey);
        cmd.Parameters.AddWithValue("o", stepOrder);
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task RecordRunFinishedAsync(
        long runId, DoTheDoRunStatus status, Guid? novaradAuditId, string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE tech_validation.do_the_do_runs
            SET status = @s,
                novarad_audit_id = @aid,
                error_message = @err,
                completed_at = NOW()
            WHERE run_id = @id
            """;
        cmd.Parameters.AddWithValue("id", runId);
        cmd.Parameters.AddWithValue("s", (short)status);
        cmd.Parameters.Add(new NpgsqlParameter("aid", NpgsqlDbType.Uuid) { Value = (object?)novaradAuditId ?? DBNull.Value });
        cmd.Parameters.Add(NullableText("err", errorMessage));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TechNotesTemplate>> ListActiveTemplatesAsync(
        Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT template_id, label, body, sort_order
            FROM tech_validation.tech_notes_templates
            WHERE is_active = TRUE
            ORDER BY sort_order, label
            """;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var list = new List<TechNotesTemplate>();
        while (await reader.ReadAsync(cancellationToken))
            list.Add(new TechNotesTemplate(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        return list;
    }

    public async Task<TechNotesTemplate> CreateTemplateAsync(
        Guid tenantId, Guid createdByUserId, string label, string body, int sortOrder,
        CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tech_validation.tech_notes_templates
                (tenant_id, label, body, sort_order, created_by_user_id)
            VALUES (@t, @label, @body, @sort, @uid)
            RETURNING template_id, label, body, sort_order
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("label", label);
        cmd.Parameters.AddWithValue("body", body);
        cmd.Parameters.AddWithValue("sort", sortOrder);
        cmd.Parameters.AddWithValue("uid", createdByUserId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new TechNotesTemplate(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3));
    }

    public async Task<TechNotesTemplate?> UpdateTemplateAsync(
        Guid tenantId, long templateId, string label, string body, int sortOrder,
        CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE tech_validation.tech_notes_templates
            SET label = @label, body = @body, sort_order = @sort, updated_at = NOW()
            WHERE template_id = @id AND tenant_id = @t AND is_active = TRUE
            RETURNING template_id, label, body, sort_order
            """;
        cmd.Parameters.AddWithValue("id", templateId);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("label", label);
        cmd.Parameters.AddWithValue("body", body);
        cmd.Parameters.AddWithValue("sort", sortOrder);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new TechNotesTemplate(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3));
    }

    public async Task<bool> DeactivateTemplateAsync(
        Guid tenantId, long templateId, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE tech_validation.tech_notes_templates
            SET is_active = FALSE, updated_at = NOW()
            WHERE template_id = @id AND tenant_id = @t AND is_active = TRUE
            """;
        cmd.Parameters.AddWithValue("id", templateId);
        cmd.Parameters.AddWithValue("t", tenantId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task UpsertReadyStudiesAsync(
        Guid tenantId, IReadOnlyCollection<ReadyStudy> studies,
        CancellationToken cancellationToken = default)
    {
        if (studies.Count == 0) return;
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        foreach (var s in studies)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tech_validation.ready_studies
                    (tenant_id, novarad_study_id, facility_id, study_uid, accession, study_date, modality,
                     custom_3, novarad_patient_id, patient_pid, patient_last_name, patient_first_name,
                     patient_birth_date, patient_gender, last_image_processed_date, study_description, projected_at)
                VALUES (@t, @sid, @fac, @uid, @acc, @sdate, @mod,
                        @c3, @pid, @ppid, @plast, @pfirst,
                        @pbirth, @pgender, @lipd, @sdesc, NOW())
                ON CONFLICT (tenant_id, novarad_study_id) DO UPDATE SET
                    facility_id              = EXCLUDED.facility_id,
                    study_uid                = EXCLUDED.study_uid,
                    accession                = EXCLUDED.accession,
                    study_date               = EXCLUDED.study_date,
                    modality                 = EXCLUDED.modality,
                    custom_3                 = EXCLUDED.custom_3,
                    novarad_patient_id       = EXCLUDED.novarad_patient_id,
                    patient_pid              = EXCLUDED.patient_pid,
                    patient_last_name        = EXCLUDED.patient_last_name,
                    patient_first_name       = EXCLUDED.patient_first_name,
                    patient_birth_date       = EXCLUDED.patient_birth_date,
                    patient_gender           = EXCLUDED.patient_gender,
                    last_image_processed_date= EXCLUDED.last_image_processed_date,
                    study_description        = EXCLUDED.study_description,
                    projected_at             = NOW()
                """;
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("sid", s.NovaradStudyId);
            cmd.Parameters.AddWithValue("fac", s.FacilityId);
            cmd.Parameters.AddWithValue("uid", s.StudyUid);
            cmd.Parameters.Add(NullableText("acc", s.Accession));
            cmd.Parameters.Add(NullableTimestamp("sdate", s.StudyDate));
            cmd.Parameters.Add(NullableText("mod", s.Modality));
            cmd.Parameters.Add(NullableText("c3", s.Custom3));
            cmd.Parameters.AddWithValue("pid", s.NovaradPatientId);
            cmd.Parameters.Add(NullableText("ppid", s.PatientPid));
            cmd.Parameters.Add(NullableText("plast", s.PatientLastName));
            cmd.Parameters.Add(NullableText("pfirst", s.PatientFirstName));
            cmd.Parameters.Add(NullableText("pgender", s.PatientGender));
            cmd.Parameters.Add(new NpgsqlParameter("pbirth", NpgsqlDbType.Date)
            {
                Value = s.PatientBirthDate.HasValue ? s.PatientBirthDate.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value
            });
            cmd.Parameters.Add(NullableTimestamp("lipd", s.LastImageProcessedDate));
            cmd.Parameters.Add(NullableText("sdesc", s.StudyDescription));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task PruneStaleReadyStudiesAsync(
        Guid tenantId, DateTimeOffset olderThan, CancellationToken cancellationToken = default)
    {
        await using var conn = (NpgsqlConnection)await _db.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM tech_validation.ready_studies rs
            WHERE rs.tenant_id = @t
              AND rs.projected_at < @cutoff
              AND NOT EXISTS (
                  SELECT 1 FROM tech_validation.validations v
                  WHERE v.tenant_id = rs.tenant_id
                    AND v.novarad_study_id = rs.novarad_study_id
                    AND v.status IN (1, 2)
              )
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("cutoff", olderThan.UtcDateTime);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    // -----------------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------------

    private const string SelectValidationSql = """
        SELECT validation_id, tenant_id, novarad_study_id, novarad_order_id, novarad_patient_id,
               current_step, status, reason, tech_notes, referring_physician_id, comparison_study_ids,
               create_order_requested, started_by_user_id, started_at, completed_at,
               patient_action, corrected_last_name, corrected_first_name, corrected_middle_name,
               corrected_birth_date, corrected_gender, reassign_target_patient_id
        FROM tech_validation.validations
        """;

    private static async Task<ValidationRecord?> ReadOneValidationAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var correctedLast = reader.IsDBNull(16) ? null : reader.GetString(16);
        var correctedFirst = reader.IsDBNull(17) ? null : reader.GetString(17);
        var correctedMiddle = reader.IsDBNull(18) ? null : reader.GetString(18);
        DateOnly? correctedBirth = reader.IsDBNull(19) ? null : DateOnly.FromDateTime(reader.GetDateTime(19));
        var correctedGender = reader.IsDBNull(20) ? null : reader.GetString(20);
        PatientCorrection? correction =
            (correctedLast is null && correctedFirst is null && correctedMiddle is null
             && correctedBirth is null && correctedGender is null)
                ? null
                : new PatientCorrection(correctedLast, correctedFirst, correctedMiddle, correctedBirth, correctedGender);

        return new ValidationRecord(
            ValidationId: reader.GetGuid(0),
            TenantId: reader.GetGuid(1),
            NovaradStudyId: reader.GetInt64(2),
            NovaradOrderId: reader.IsDBNull(3) ? null : reader.GetInt64(3),
            NovaradPatientId: reader.IsDBNull(4) ? null : reader.GetInt64(4),
            CurrentStep: (WizardStep)reader.GetInt16(5),
            Status: (ValidationStatus)reader.GetInt16(6),
            Reason: reader.IsDBNull(7) ? null : reader.GetString(7),
            TechNotes: reader.IsDBNull(8) ? null : reader.GetString(8),
            ReferringPhysicianId: reader.IsDBNull(9) ? null : reader.GetInt64(9),
            ComparisonStudyIds: reader.IsDBNull(10) ? Array.Empty<long>() : (long[])reader.GetValue(10),
            CreateOrderRequested: reader.GetBoolean(11),
            StartedByUserId: reader.GetGuid(12),
            StartedAt: new DateTimeOffset(reader.GetDateTime(13), TimeSpan.Zero),
            CompletedAt: reader.IsDBNull(14) ? null : new DateTimeOffset(reader.GetDateTime(14), TimeSpan.Zero),
            PatientAction: (PatientAction)reader.GetInt16(15),
            Correction: correction,
            ReassignTargetPatientId: reader.IsDBNull(21) ? null : reader.GetInt64(21));
    }

    private static NpgsqlParameter NullableText(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };

    private static NpgsqlParameter NullableBigInt(string name, long? value) =>
        new(name, NpgsqlDbType.Bigint) { Value = (object?)value ?? DBNull.Value };

    private static NpgsqlParameter NullableTimestamp(string name, DateTimeOffset? value) =>
        new(name, NpgsqlDbType.TimestampTz) { Value = (object?)value?.UtcDateTime ?? DBNull.Value };

    private static NpgsqlParameter NullableDate(string name, DateOnly? value) =>
        new(name, NpgsqlDbType.Date)
        {
            Value = value.HasValue ? value.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value
        };
}
