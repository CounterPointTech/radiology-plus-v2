"use client";

import { Button } from "@/components/ui/button";
import { Card, CardBody, CardHeader, CardSubtitle, CardTitle } from "@/components/ui/card";
import type { CorrectionDraft } from "@/components/wizard/step-study";
import {
  PatientAction,
  type CandidateOrder,
  type PatientSearchResult,
  type ReadyStudy,
} from "@/lib/types";
import { formatDate, fullName } from "@/lib/utils";

export function StepReview({
  study,
  order,
  reason,
  techNotes,
  createOrderRequested,
  patientAction,
  correction,
  reassignTarget,
  reassignTargetPatientId,
  onBack,
  onContinue,
  saving,
}: {
  study: ReadyStudy | null;
  order: CandidateOrder | null;
  reason: string;
  techNotes: string;
  createOrderRequested: boolean;
  patientAction: number;
  correction: CorrectionDraft;
  reassignTarget: PatientSearchResult | null;
  reassignTargetPatientId: number | null;
  onBack: () => void;
  onContinue: () => void;
  saving: boolean;
}) {
  const isEdit = patientAction === PatientAction.EditInPlace;
  const isReassign = patientAction === PatientAction.Reassign;

  const nextLast = isEdit ? correction.lastName.trim() : "";
  const nextFirst = isEdit ? correction.firstName.trim() : "";
  const nextMiddle = isEdit ? correction.middleName.trim() : "";
  const nextDob = isEdit ? correction.birthDate.trim() : "";
  const nextSex = isEdit ? correction.gender.trim() : "";

  const curName = fullName(study?.patientLastName, study?.patientFirstName);
  const nameChanged =
    isEdit &&
    ((!!nextLast && nextLast !== (study?.patientLastName ?? "")) ||
      (!!nextFirst && nextFirst !== (study?.patientFirstName ?? "")) ||
      !!nextMiddle);
  const newName =
    fullName(
      (nextLast || study?.patientLastName) ?? null,
      (nextFirst || study?.patientFirstName) ?? null,
    ) + (nextMiddle ? ` ${nextMiddle}` : "");
  const dobChanged = isEdit && !!nextDob && nextDob !== (study?.patientBirthDate ?? "");
  const sexChanged = isEdit && !!nextSex && nextSex !== (study?.patientGender ?? "");

  const patientTitle = isEdit
    ? "Patient · editing"
    : isReassign
      ? "Patient · reassigning"
      : "Patient";

  return (
    <Card>
      <CardHeader>
        <CardTitle>Review</CardTitle>
        <CardSubtitle>
          Last look before you finalize. Confirm the details below are correct.
        </CardSubtitle>
      </CardHeader>
      <CardBody className="space-y-5">
        {study ? (
          <Section title={patientTitle}>
            {isReassign ? (
              <div className="space-y-3">
                <p className="text-xs text-[color:var(--color-caution)]">
                  This study is moving to a different patient — its images move with it.
                </p>
                <dl className="grid grid-cols-1 sm:grid-cols-2 gap-y-2 gap-x-6 text-sm">
                  <Item label="Currently">
                    <span className="text-[color:var(--color-muted-fg)] line-through">
                      {fullName(study.patientLastName, study.patientFirstName)} ·{" "}
                      {study.patientPid ?? "—"}
                    </span>
                  </Item>
                  <Item label="New patient" changed>
                    <span className="font-medium text-[color:var(--color-caution)]">
                      {reassignTarget
                        ? `${fullName(reassignTarget.lastName, reassignTarget.firstName)} · ${reassignTarget.pid ?? "—"}`
                        : reassignTargetPatientId
                          ? `Novarad patient #${reassignTargetPatientId}`
                          : "—"}
                    </span>
                  </Item>
                  {reassignTarget ? (
                    <Item label="New DOB" changed>
                      <span className="text-[color:var(--color-caution)]">
                        {formatDate(reassignTarget.birthDate)}
                      </span>
                    </Item>
                  ) : null}
                  {reassignTarget?.gender ? (
                    <Item label="New sex" changed>
                      <span className="text-[color:var(--color-caution)]">
                        {reassignTarget.gender}
                      </span>
                    </Item>
                  ) : null}
                </dl>
              </div>
            ) : (
              <dl className="grid grid-cols-1 sm:grid-cols-2 gap-y-2 gap-x-6 text-sm">
                <Item label="Name" changed={nameChanged}>
                  {nameChanged ? <Changed next={newName} was={curName} /> : curName}
                </Item>
                <Item label="Patient ID">
                  <span className="font-mono">{study.patientPid ?? "—"}</span>
                </Item>
                <Item label="DOB" changed={dobChanged}>
                  {dobChanged ? (
                    <Changed
                      next={formatDate(nextDob)}
                      was={formatDate(study.patientBirthDate)}
                    />
                  ) : (
                    formatDate(study.patientBirthDate)
                  )}
                </Item>
                {nextMiddle ? (
                  <Item label="Middle name" changed>
                    <Changed next={nextMiddle} was={null} />
                  </Item>
                ) : null}
                <Item label="Sex" changed={sexChanged}>
                  {sexChanged ? (
                    <Changed next={nextSex} was={study.patientGender ?? "—"} />
                  ) : (
                    study.patientGender ?? "—"
                  )}
                </Item>
              </dl>
            )}
          </Section>
        ) : null}

        <Section title="Study">
          {study ? (
            <dl className="grid grid-cols-1 sm:grid-cols-2 gap-y-2 gap-x-6 text-sm">
              <Item label="Modality">{study.modality ?? "—"}</Item>
              <Item label="Study date">{formatDate(study.studyDate)}</Item>
              <Item label="Accession">
                <span className="font-mono">{study.accession ?? "—"}</span>
              </Item>
            </dl>
          ) : null}
        </Section>

        <Section title="Order">
          {order ? (
            <dl className="grid grid-cols-1 sm:grid-cols-2 gap-y-2 gap-x-6 text-sm">
              <Item label="Accession">
                <span className="font-mono">{order.accessionNumber}</span>
              </Item>
              <Item label="Status">{order.status}</Item>
              <Item label="Description">{order.description ?? "—"}</Item>
              <Item label="Order ID">#{order.orderId}</Item>
            </dl>
          ) : createOrderRequested ? (
            <p className="text-sm text-[color:var(--color-caution)]">
              Order will be requested as a follow-up. RIS-side writes are skipped.
            </p>
          ) : (
            <p className="text-sm text-[color:var(--color-muted-fg)]">No order selected.</p>
          )}
        </Section>

        <Section title="Reason">
          <p className="text-sm whitespace-pre-wrap">{reason || "—"}</p>
        </Section>

        {techNotes ? (
          <Section title="Tech notes (internal)">
            <p className="text-sm whitespace-pre-wrap text-[color:var(--color-muted-fg)]">
              {techNotes}
            </p>
          </Section>
        ) : null}

        <Section title="When you finalize">
          <ul className="text-sm space-y-1.5 list-disc pl-5 marker:text-[color:var(--color-accent)]">
            <li>Mark this study as validated and confirm the patient record.</li>
            {patientAction === PatientAction.EditInPlace ? (
              <li>Update the patient&apos;s details on this study.</li>
            ) : patientAction === PatientAction.Reassign ? (
              <li>Move this study to the selected patient.</li>
            ) : null}
            {order ? (
              <li>
                Attach the study to order{" "}
                <span className="font-mono">{order.accessionNumber}</span> and
                add your reason to the order.
              </li>
            ) : createOrderRequested ? (
              <li>Flag that an order still needs to be created for this study.</li>
            ) : (
              <li>No order changes (none selected).</li>
            )}
          </ul>
        </Section>
      </CardBody>
      <div className="px-5 py-3 border-t border-[color:var(--color-border)] flex items-center justify-between">
        <Button variant="ghost" onClick={onBack}>
          Back
        </Button>
        <Button onClick={onContinue} loading={saving}>
          Continue to finalize
        </Button>
      </div>
    </Card>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section>
      <h3 className="text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)] mb-2">
        {title}
      </h3>
      <div className="rounded-md border border-[color:var(--color-border)] p-3 bg-[color:var(--color-surface-2)]/40">
        {children}
      </div>
    </section>
  );
}

function Item({
  label,
  children,
  changed,
}: {
  label: string;
  children: React.ReactNode;
  changed?: boolean;
}) {
  return (
    <div
      className={
        changed
          ? "rounded-sm -mx-1.5 px-1.5 py-0.5 bg-[color:var(--color-accent)]/10 border-l-2 border-[color:var(--color-accent)]"
          : undefined
      }
    >
      <dt className="text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)]">
        {label}
      </dt>
      <dd className="mt-0.5">{children}</dd>
    </div>
  );
}

function Changed({ next, was }: { next: React.ReactNode; was: React.ReactNode }) {
  return (
    <span className="font-medium text-[color:var(--color-accent)]">
      {next}
      {was ? (
        <span className="ml-2 text-xs font-normal text-[color:var(--color-muted-fg)] line-through">
          {was}
        </span>
      ) : null}
    </span>
  );
}
