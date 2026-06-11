using System.Data;
using System.Globalization;
using Microsoft.Extensions.Logging;
#pragma warning disable CA1848 // LoggerMessage delegates are perf-advisory, not reliability.
using Npgsql;
using NpgsqlTypes;
using RadiologyPlus.Core.Audit;
using RadiologyPlus.Core.TechValidation;
using RadiologyPlus.Core.Tenancy;

namespace RadiologyPlus.Data.TechValidation;

/// <summary>
/// Ordered PACS+RIS write sequence from the whiteboard's "Doing the Do!" panel.
///
///  PACS (Novarad pacs.studies / pacs.patients):
///   1. Correct patient information on the study (set is_valid=TRUE, refresh comments per reason)
///   2. Merge comparison studies (mark comparisons as is_study_of_interest=TRUE on the chosen comparisons)
///   3. Mark the primary study verified (pacs.patients.is_verified for the patient)
///
///  RIS (Novarad ris.orders / ris.order_procedures):
///   4. Put the chosen StudyUID on the selected order's procedure row (ris.order_procedures.study_uid)
///   5. Save the wizard's reason text onto the order (ris.orders.notes, append-mode)
///
/// FFI (3rd-party SQL Server) is deferred to a follow-up — schema lives outside this DB.
///
/// Each step uses <see cref="INovaradWriter"/> so we get dual audit + transactional safety.
/// On any failure we mark the validation Failed and emit a final progress event.
/// </summary>
public sealed class DoTheDoOrchestrator : IDoTheDoOrchestrator
{
    private readonly ITechValidationRepository _repo;
    private readonly INovaradWriter _writer;
    private readonly INovaradStudyReader _reader;
    private readonly ITenantContextAccessor _tenants;
    private readonly ILogger<DoTheDoOrchestrator> _logger;

    public DoTheDoOrchestrator(
        ITechValidationRepository repo,
        INovaradWriter writer,
        INovaradStudyReader reader,
        ITenantContextAccessor tenants,
        ILogger<DoTheDoOrchestrator> logger)
    {
        _repo = repo;
        _writer = writer;
        _reader = reader;
        _tenants = tenants;
        _logger = logger;
    }

    public async Task<DoTheDoOutcome> RunAsync(
        Guid validationId,
        IProgress<DoTheDoProgressEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var tenant = _tenants.Require();
        var validation = await _repo.GetValidationAsync(tenant.TenantId, validationId, cancellationToken)
            ?? throw new InvalidOperationException($"Validation {validationId} not found in tenant {tenant.TenantId}.");

        if (validation.Status is not (ValidationStatus.Open or ValidationStatus.InProgress))
        {
            throw new InvalidOperationException(
                $"Validation {validationId} is not eligible for Do-the-Do (status={validation.Status}).");
        }

        await _repo.SetStatusAsync(tenant.TenantId, validationId, ValidationStatus.Submitted, cancellationToken);

        var steps = BuildSteps(validation);
        var completed = 0;

        foreach (var step in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var runId = await _repo.RecordRunStartedAsync(
                tenant.TenantId, validationId, step.Key, step.Order, cancellationToken);

            progress?.Report(new DoTheDoProgressEvent(
                validationId, step.Order, steps.Count, step.Key, step.Description,
                DoTheDoRunStatus.Started, null));

            try
            {
                var audit = await step.ExecuteAsync(_writer, validation, cancellationToken);
                await _repo.RecordRunFinishedAsync(
                    runId, DoTheDoRunStatus.Succeeded, novaradAuditId: null, errorMessage: null, cancellationToken);

                completed++;
                progress?.Report(new DoTheDoProgressEvent(
                    validationId, step.Order, steps.Count, step.Key, step.Description,
                    DoTheDoRunStatus.Succeeded, null));

                _logger.LogInformation(
                    "Do-the-Do step {Order}/{Total} '{Key}' completed (validation={Validation}).",
                    step.Order, steps.Count, step.Key, validationId);
                _ = audit;
            }
            catch (Exception ex)
            {
                await _repo.RecordRunFinishedAsync(
                    runId, DoTheDoRunStatus.Failed, novaradAuditId: null, errorMessage: ex.Message, cancellationToken);
                await _repo.SetStatusAsync(tenant.TenantId, validationId, ValidationStatus.Failed, cancellationToken);

                progress?.Report(new DoTheDoProgressEvent(
                    validationId, step.Order, steps.Count, step.Key, step.Description,
                    DoTheDoRunStatus.Failed, ex.Message));

                _logger.LogError(ex,
                    "Do-the-Do step {Order}/{Total} '{Key}' failed (validation={Validation}); aborting sequence.",
                    step.Order, steps.Count, step.Key, validationId);

                return new DoTheDoOutcome(validationId, false, completed, steps.Count, ex.Message);
            }
        }

        await _repo.MarkCompletedAsync(tenant.TenantId, validationId, cancellationToken);

        // A patient correction/reassignment changes what the worklist projection shows
        // (demographics, or which patient the study belongs to). Re-read the one study
        // from Novarad and refresh the snapshot so the UI is immediately consistent
        // instead of waiting for the next projector pass. Best-effort: the authoritative
        // Novarad write already committed, so a refresh failure must not fail the finalize.
        if (validation.PatientAction != PatientAction.None)
        {
            try
            {
                var fresh = await _reader.ReadStudyByIdAsync(validation.NovaradStudyId, cancellationToken);
                if (fresh is not null)
                    await _repo.UpsertReadyStudiesAsync(tenant.TenantId, new[] { fresh }, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Post-finalize projection refresh failed for study {Study} (validation {Validation}); " +
                    "worklist will self-heal on the next projector pass.",
                    validation.NovaradStudyId, validationId);
            }
        }

        return new DoTheDoOutcome(validationId, true, completed, steps.Count, null);
    }

    private static List<DoTheDoStep> BuildSteps(ValidationRecord v)
    {
        var steps = new List<DoTheDoStep>
        {
            new(
                Order: 0,
                Key: "pacs.correct_patient_info",
                Description: "Confirming patient information on the study",
                ExecuteAsync: (w, val, ct) => w.ExecuteAsync(
                    action: async (conn, tx, c) =>
                    {
                        await using var cmd = ((NpgsqlConnection)conn).CreateCommand();
                        cmd.Transaction = (NpgsqlTransaction)tx;
                        cmd.CommandText = """
                            UPDATE pacs.studies SET is_valid = TRUE,
                                comments = COALESCE(comments, '') || E'\nRadiologyPlus: ' || @reason,
                                modified_date = LOCALTIMESTAMP
                            WHERE id = @s
                            """;
                        cmd.Parameters.AddWithValue("s", val.NovaradStudyId);
                        cmd.Parameters.AddWithValue("reason", val.Reason ?? "");
                        await cmd.ExecuteNonQueryAsync(c);
                        return null;
                    },
                    description: "PACS: correct patient info on study",
                    reason: ReasonOrFallback(val),
                    resourceType: "pacs.studies",
                    resourceId: val.NovaradStudyId.ToString(CultureInfo.InvariantCulture),
                    cancellationToken: ct)),
        };

        // Optional patient action (chosen on step 1): correct demographics on the study's
        // current patient, OR move the study to a different existing patient. Each is a
        // single dual-audited Novarad write; we capture a before-snapshot for the audit log.
        if (v.PatientAction == PatientAction.EditInPlace && v.Correction is not null && v.NovaradPatientId is not null)
        {
            steps.Add(new DoTheDoStep(
                Order: 0,
                Key: "pacs.edit_patient_demographics",
                Description: "Updating the patient's details",
                ExecuteAsync: (w, val, ct) => w.ExecuteAsync(
                    action: async (conn, tx, c) =>
                    {
                        var npg = (NpgsqlConnection)conn;
                        var corr = val.Correction!;

                        object? before = null;
                        await using (var sel = npg.CreateCommand())
                        {
                            sel.Transaction = (NpgsqlTransaction)tx;
                            sel.CommandText = """
                                SELECT last_name::text, first_name::text, middle_name::text, birth_time, gender::text
                                FROM pacs.patients WHERE id = @p FOR UPDATE
                                """;
                            sel.Parameters.AddWithValue("p", val.NovaradPatientId!.Value);
                            await using var r = await sel.ExecuteReaderAsync(c);
                            if (await r.ReadAsync(c))
                            {
                                before = new
                                {
                                    lastName = r.IsDBNull(0) ? null : r.GetString(0),
                                    firstName = r.IsDBNull(1) ? null : r.GetString(1),
                                    middleName = r.IsDBNull(2) ? null : r.GetString(2),
                                    birthDate = r.IsDBNull(3) ? (DateTime?)null : r.GetDateTime(3),
                                    gender = r.IsDBNull(4) ? null : r.GetString(4),
                                };
                            }
                        }

                        await using var cmd = npg.CreateCommand();
                        cmd.Transaction = (NpgsqlTransaction)tx;
                        cmd.CommandText = """
                            UPDATE pacs.patients SET
                                last_name     = COALESCE(@ln, last_name),
                                first_name    = COALESCE(@fn, first_name),
                                middle_name   = COALESCE(@mn, middle_name),
                                birth_time    = COALESCE(@dob, birth_time),
                                gender        = COALESCE(@sex, gender),
                                modified_date = LOCALTIMESTAMP
                            WHERE id = @p
                            """;
                        cmd.Parameters.AddWithValue("p", val.NovaradPatientId!.Value);
                        cmd.Parameters.Add(TextParam("ln", corr.LastName));
                        cmd.Parameters.Add(TextParam("fn", corr.FirstName));
                        cmd.Parameters.Add(TextParam("mn", corr.MiddleName));
                        cmd.Parameters.Add(new NpgsqlParameter("dob", NpgsqlDbType.Timestamp)
                        {
                            Value = corr.BirthDate.HasValue ? corr.BirthDate.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value
                        });
                        cmd.Parameters.Add(TextParam("sex", corr.Gender));
                        await cmd.ExecuteNonQueryAsync(c);

                        return new { before, after = corr };
                    },
                    description: "PACS: edit patient demographics",
                    reason: ReasonOrFallback(val),
                    resourceType: "pacs.patients",
                    resourceId: val.NovaradPatientId!.Value.ToString(CultureInfo.InvariantCulture),
                    cancellationToken: ct)));
        }
        else if (v.PatientAction == PatientAction.Reassign && v.ReassignTargetPatientId is not null)
        {
            steps.Add(new DoTheDoStep(
                Order: 0,
                Key: "pacs.reassign_study_patient",
                Description: "Reassigning the study to the correct patient",
                ExecuteAsync: (w, val, ct) => w.ExecuteAsync(
                    action: async (conn, tx, c) =>
                    {
                        var npg = (NpgsqlConnection)conn;

                        object? before = null;
                        await using (var sel = npg.CreateCommand())
                        {
                            sel.Transaction = (NpgsqlTransaction)tx;
                            sel.CommandText = "SELECT patient FROM pacs.studies WHERE id = @s FOR UPDATE";
                            sel.Parameters.AddWithValue("s", val.NovaradStudyId);
                            var cur = await sel.ExecuteScalarAsync(c);
                            before = new { patient = cur is long l ? l : (long?)null };
                        }

                        await using var cmd = npg.CreateCommand();
                        cmd.Transaction = (NpgsqlTransaction)tx;
                        cmd.CommandText = """
                            UPDATE pacs.studies SET patient = @target, modified_date = LOCALTIMESTAMP
                            WHERE id = @s
                            """;
                        cmd.Parameters.AddWithValue("target", val.ReassignTargetPatientId!.Value);
                        cmd.Parameters.AddWithValue("s", val.NovaradStudyId);
                        await cmd.ExecuteNonQueryAsync(c);

                        return new { before, after = new { patient = val.ReassignTargetPatientId!.Value } };
                    },
                    description: "PACS: reassign study to patient",
                    reason: ReasonOrFallback(val),
                    resourceType: "pacs.studies",
                    resourceId: val.NovaradStudyId.ToString(CultureInfo.InvariantCulture),
                    cancellationToken: ct)));
        }

        steps.Add(new DoTheDoStep(
            Order: 0,
            Key: "pacs.merge_comparisons",
            Description: "Flagging comparison studies",
            ExecuteAsync: (w, val, ct) =>
            {
                if (val.ComparisonStudyIds.Length == 0)
                {
                    // No-op marker — still write an audit row so the audit shows we considered the step.
                    return w.ExecuteAsync(
                        action: (_, _, _) => Task.FromResult<object?>(null),
                        description: "PACS: no comparisons selected (no-op)",
                        reason: ReasonOrFallback(val),
                        resourceType: "pacs.studies",
                        resourceId: val.NovaradStudyId.ToString(CultureInfo.InvariantCulture),
                        cancellationToken: ct);
                }
                return w.ExecuteAsync(
                    action: async (conn, tx, c) =>
                    {
                        await using var cmd = ((NpgsqlConnection)conn).CreateCommand();
                        cmd.Transaction = (NpgsqlTransaction)tx;
                        cmd.CommandText = """
                            UPDATE pacs.studies SET is_study_of_interest = TRUE,
                                modified_date = LOCALTIMESTAMP
                            WHERE id = ANY(@ids)
                            """;
                        cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
                        { Value = val.ComparisonStudyIds });
                        await cmd.ExecuteNonQueryAsync(c);
                        return null;
                    },
                    description: "PACS: mark comparison studies as of-interest",
                    reason: ReasonOrFallback(val),
                    resourceType: "pacs.studies",
                    resourceId: string.Join(",", val.ComparisonStudyIds),
                    cancellationToken: ct);
            }));

        // TODO(FFI-comparisons): once the integration channel is chosen, insert an
        // additional Do-the-Do step here that resolves IFfiComparisonSink from DI and
        // calls SubmitFlaggedComparisonsAsync(val.ValidationId, val.ComparisonStudyIds).
        // The NoOpFfiComparisonSink currently registered keeps the orchestrator stable
        // until then. See IFfiComparisonSink for the three candidate routes.

        steps.Add(new DoTheDoStep(
            Order: 0,
            Key: "pacs.mark_patient_verified",
            Description: "Verifying the patient record",
            ExecuteAsync: (w, val, ct) =>
            {
                // Verify the patient the study now belongs to: the reassignment target
                // when reassigning, otherwise the study's current patient.
                var patientId = EffectivePatientId(val);
                if (patientId is null)
                {
                    throw new InvalidOperationException("Cannot mark patient verified: no patient_id on validation.");
                }
                return w.ExecuteAsync(
                    action: async (conn, tx, c) =>
                    {
                        await using var cmd = ((NpgsqlConnection)conn).CreateCommand();
                        cmd.Transaction = (NpgsqlTransaction)tx;
                        cmd.CommandText = """
                            UPDATE pacs.patients SET is_verified = TRUE, modified_date = LOCALTIMESTAMP
                            WHERE id = @p
                            """;
                        cmd.Parameters.AddWithValue("p", patientId.Value);
                        await cmd.ExecuteNonQueryAsync(c);
                        return null;
                    },
                    description: "PACS: mark patient verified",
                    reason: ReasonOrFallback(val),
                    resourceType: "pacs.patients",
                    resourceId: patientId.Value.ToString(CultureInfo.InvariantCulture),
                    cancellationToken: ct);
            }));

        if (v.NovaradOrderId is not null)
        {
            steps.Add(new DoTheDoStep(
                Order: 4,
                Key: "ris.put_study_uid_on_order_procedure",
                Description: "Linking the study to the order",
                ExecuteAsync: (w, val, ct) => w.ExecuteAsync(
                    action: async (conn, tx, c) =>
                    {
                        await using var cmd = ((NpgsqlConnection)conn).CreateCommand();
                        cmd.Transaction = (NpgsqlTransaction)tx;
                        cmd.CommandText = """
                            UPDATE ris.order_procedures op
                            SET study_uid = s.study_uid, modified_date = LOCALTIMESTAMP
                            FROM pacs.studies s
                            WHERE op.order_id = @o AND s.id = @sid
                            """;
                        cmd.Parameters.AddWithValue("o", val.NovaradOrderId!.Value);
                        cmd.Parameters.AddWithValue("sid", val.NovaradStudyId);
                        await cmd.ExecuteNonQueryAsync(c);
                        return null;
                    },
                    description: "RIS: put StudyUID on order procedure",
                    reason: ReasonOrFallback(val),
                    resourceType: "ris.order_procedures",
                    resourceId: val.NovaradOrderId!.Value.ToString(CultureInfo.InvariantCulture),
                    cancellationToken: ct)));

            steps.Add(new DoTheDoStep(
                Order: 5,
                Key: "ris.append_reason_to_order_notes",
                Description: "Saving your reason to the order",
                ExecuteAsync: (w, val, ct) => w.ExecuteAsync(
                    action: async (conn, tx, c) =>
                    {
                        await using var cmd = ((NpgsqlConnection)conn).CreateCommand();
                        cmd.Transaction = (NpgsqlTransaction)tx;
                        cmd.CommandText = """
                            UPDATE ris.orders
                            SET notes = COALESCE(notes, '') || E'\nRadiologyPlus: ' || @reason
                            WHERE order_id = @o
                            """;
                        cmd.Parameters.AddWithValue("o", val.NovaradOrderId!.Value);
                        cmd.Parameters.AddWithValue("reason", val.Reason ?? "");
                        await cmd.ExecuteNonQueryAsync(c);
                        return null;
                    },
                    description: "RIS: append reason to order notes",
                    reason: ReasonOrFallback(val),
                    resourceType: "ris.orders",
                    resourceId: val.NovaradOrderId!.Value.ToString(CultureInfo.InvariantCulture),
                    cancellationToken: ct)));
        }

        // Assign sequential 1..N order now that the conditional steps are known.
        for (int i = 0; i < steps.Count; i++)
            steps[i] = steps[i] with { Order = i + 1 };

        return steps;
    }

    private static long? EffectivePatientId(ValidationRecord v) =>
        v.PatientAction == PatientAction.Reassign && v.ReassignTargetPatientId is not null
            ? v.ReassignTargetPatientId
            : v.NovaradPatientId;

    private static NpgsqlParameter TextParam(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };

    private static string ReasonOrFallback(ValidationRecord v) =>
        !string.IsNullOrWhiteSpace(v.Reason) ? v.Reason!
        : !string.IsNullOrWhiteSpace(v.TechNotes) ? v.TechNotes!
        : "Tech validation (no reason supplied)";

    private sealed record DoTheDoStep(
        int Order,
        string Key,
        string Description,
        Func<INovaradWriter, ValidationRecord, CancellationToken, Task<NovaradAuditRecord>> ExecuteAsync);
}
