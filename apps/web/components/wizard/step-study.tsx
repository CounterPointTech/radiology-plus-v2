"use client";

import { useQuery } from "@tanstack/react-query";
import { Check, Image, Search, User, UserCog, X } from "lucide-react";
import { useState } from "react";

import { Button } from "@/components/ui/button";
import { Card, CardBody, CardHeader, CardSubtitle, CardTitle } from "@/components/ui/card";
import { Input, Label } from "@/components/ui/input";
import { Spinner } from "@/components/ui/spinner";
import { techApi } from "@/lib/api";
import { PatientAction, type PatientSearchResult, type ReadyStudy } from "@/lib/types";
import { cn, computeAge, formatDate, fullName } from "@/lib/utils";

export interface CorrectionDraft {
  lastName: string;
  firstName: string;
  middleName: string;
  birthDate: string;
  gender: string;
}

export function StepStudy({
  study,
  patientAction,
  correction,
  reassignTarget,
  onChangeAction,
  onChangeCorrection,
  onChangeReassignTarget,
  onContinue,
  saving,
}: {
  study: ReadyStudy | null;
  patientAction: number;
  correction: CorrectionDraft;
  reassignTarget: PatientSearchResult | null;
  onChangeAction: (action: number) => void;
  onChangeCorrection: (next: CorrectionDraft) => void;
  onChangeReassignTarget: (target: PatientSearchResult | null) => void;
  onContinue: () => void;
  saving: boolean;
}) {
  // The parent owns action switching (it resets/pre-fills the draft atomically).
  const reassignReady =
    patientAction !== PatientAction.Reassign || reassignTarget !== null;

  return (
    <Card>
      <CardHeader>
        <CardTitle>Confirm the study</CardTitle>
        <CardSubtitle>
          This is the PACS row your wizard is anchored to. Make sure the patient and exam
          line up with what you expect before moving on.
        </CardSubtitle>
      </CardHeader>
      <CardBody className="space-y-6">
        {!study ? (
          <p className="text-sm text-[color:var(--color-muted-fg)]">
            Loading study details…
          </p>
        ) : (
          <>
            <Group icon={<User className="size-4" />} title="Patient">
              <Cell label="Name">
                {fullName(study.patientLastName, study.patientFirstName)}
              </Cell>
              <Cell label="Patient ID">
                <span className="font-mono">{study.patientPid ?? "—"}</span>
                <span className="text-xs text-[color:var(--color-muted-fg)]">
                  {" "}· Novarad #{study.novaradPatientId}
                </span>
              </Cell>
              <Cell label="DOB">
                {formatDate(study.patientBirthDate)}
                <span className="text-xs text-[color:var(--color-muted-fg)]">
                  {" "}· {computeAge(study.patientBirthDate)}
                </span>
              </Cell>
              <Cell label="Sex">{study.patientGender ?? "—"}</Cell>
            </Group>

            <Group icon={<Image className="size-4" />} title="Study">
              <Cell label="Modality">{study.modality ?? "—"}</Cell>
              <Cell label="Study date">{formatDate(study.studyDate)}</Cell>
              <Cell label="Accession">
                <span className="font-mono">{study.accession ?? "—"}</span>
              </Cell>
              <Cell label="StudyUID">
                <span className="font-mono text-xs break-all">{study.studyUid}</span>
              </Cell>
            </Group>

            <div className="space-y-3">
              <Label>Is the patient right?</Label>
              <div className="grid grid-cols-1 sm:grid-cols-3 gap-2">
                <ActionTile
                  active={patientAction === PatientAction.None}
                  icon={<Check className="size-4" />}
                  title="Looks correct"
                  subtitle="No changes needed"
                  onClick={() => onChangeAction(PatientAction.None)}
                />
                <ActionTile
                  active={patientAction === PatientAction.EditInPlace}
                  icon={<UserCog className="size-4" />}
                  title="Fix patient details"
                  subtitle="Name, DOB or sex"
                  onClick={() => onChangeAction(PatientAction.EditInPlace)}
                />
                <ActionTile
                  active={patientAction === PatientAction.Reassign}
                  icon={<User className="size-4" />}
                  title="Wrong patient"
                  subtitle="Move to another patient"
                  onClick={() => onChangeAction(PatientAction.Reassign)}
                />
              </div>

              {patientAction === PatientAction.EditInPlace ? (
                <EditPatientForm
                  correction={correction}
                  onChange={onChangeCorrection}
                />
              ) : null}

              {patientAction === PatientAction.Reassign ? (
                <ReassignPicker
                  currentStudy={study}
                  target={reassignTarget}
                  onPick={onChangeReassignTarget}
                />
              ) : null}
            </div>

            <div className="flex justify-end">
              <Button onClick={onContinue} loading={saving} disabled={!reassignReady}>
                Continue to order
              </Button>
            </div>
          </>
        )}
      </CardBody>
    </Card>
  );
}

function ActionTile({
  active,
  icon,
  title,
  subtitle,
  onClick,
}: {
  active: boolean;
  icon: React.ReactNode;
  title: string;
  subtitle: string;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={cn(
        "rounded-md border p-3 text-left transition-colors",
        active
          ? "border-[color:var(--color-accent)] bg-[color:var(--color-accent)]/10"
          : "border-[color:var(--color-border)] hover:bg-[color:var(--color-surface-2)]/40",
      )}
    >
      <div className="flex items-center gap-2 text-sm font-medium">
        {icon}
        {title}
      </div>
      <div className="text-xs text-[color:var(--color-muted-fg)] mt-0.5">{subtitle}</div>
    </button>
  );
}

function EditPatientForm({
  correction,
  onChange,
}: {
  correction: CorrectionDraft;
  onChange: (next: CorrectionDraft) => void;
}) {
  const selectClass =
    "block w-full h-10 rounded-md border border-[color:var(--color-border)] bg-[color:var(--color-surface)] px-3 py-2 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-accent)]/60";
  return (
    <div className="rounded-md border border-[color:var(--color-border)] p-4 bg-[color:var(--color-surface-2)]/30 space-y-4">
      <p className="text-xs text-[color:var(--color-muted-fg)]">
        Correct the patient on this study. Blank fields are left as-is.
      </p>
      <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
        <div className="space-y-1.5">
          <Label htmlFor="ln">Last name</Label>
          <Input
            id="ln"
            value={correction.lastName}
            onChange={(e) => onChange({ ...correction, lastName: e.target.value })}
          />
        </div>
        <div className="space-y-1.5">
          <Label htmlFor="fn">First name</Label>
          <Input
            id="fn"
            value={correction.firstName}
            onChange={(e) => onChange({ ...correction, firstName: e.target.value })}
          />
        </div>
        <div className="space-y-1.5">
          <Label htmlFor="mn">Middle name</Label>
          <Input
            id="mn"
            value={correction.middleName}
            onChange={(e) => onChange({ ...correction, middleName: e.target.value })}
          />
        </div>
        <div className="space-y-1.5">
          <Label htmlFor="dob">Date of birth</Label>
          <Input
            id="dob"
            type="date"
            value={correction.birthDate}
            onChange={(e) => onChange({ ...correction, birthDate: e.target.value })}
          />
        </div>
        <div className="space-y-1.5">
          <Label htmlFor="sex">Sex</Label>
          <select
            id="sex"
            className={selectClass}
            value={correction.gender}
            onChange={(e) => onChange({ ...correction, gender: e.target.value })}
          >
            <option value="">— no change —</option>
            <option value="M">M</option>
            <option value="F">F</option>
            <option value="O">O</option>
          </select>
        </div>
      </div>
    </div>
  );
}

function ReassignPicker({
  currentStudy,
  target,
  onPick,
}: {
  currentStudy: ReadyStudy;
  target: PatientSearchResult | null;
  onPick: (target: PatientSearchResult | null) => void;
}) {
  const [query, setQuery] = useState("");
  const trimmed = query.trim();

  const searchQuery = useQuery({
    queryKey: ["patient-search", trimmed],
    queryFn: () => techApi.patientSearch(trimmed),
    enabled: trimmed.length >= 2 && target === null,
    staleTime: 30_000,
  });

  const results = (searchQuery.data ?? []).filter(
    (r) => r.novaradPatientId !== currentStudy.novaradPatientId,
  );

  if (target) {
    return (
      <div className="rounded-md border border-[color:var(--color-caution)]/50 p-4 bg-[color:var(--color-caution)]/5 space-y-2">
        <div className="flex items-start justify-between gap-3">
          <div>
            <div className="text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)]">
              Reassign to
            </div>
            <div className="text-base font-medium mt-1">
              {fullName(target.lastName, target.firstName)}
            </div>
            <div className="text-xs font-mono text-[color:var(--color-muted-fg)]">
              PID {target.pid ?? "—"} · Novarad #{target.novaradPatientId} ·{" "}
              DOB {formatDate(target.birthDate)}
            </div>
          </div>
          <Button variant="ghost" size="sm" onClick={() => onPick(null)}>
            <X className="size-4" />
            Change
          </Button>
        </div>
        <p className="text-xs text-[color:var(--color-caution)]">
          This moves the study (and its images) from{" "}
          {fullName(currentStudy.patientLastName, currentStudy.patientFirstName)} to the
          patient above. Double-check this is the right person.
        </p>
      </div>
    );
  }

  return (
    <div className="rounded-md border border-[color:var(--color-border)] p-4 bg-[color:var(--color-surface-2)]/30 space-y-3">
      <div className="space-y-1.5">
        <Label htmlFor="psearch">Find the correct patient</Label>
        <div className="relative">
          <Search className="size-4 absolute left-3 top-1/2 -translate-y-1/2 text-[color:var(--color-muted-fg)]" />
          <Input
            id="psearch"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="Search by name or PID…"
            className="pl-9"
            autoComplete="off"
          />
        </div>
      </div>

      {trimmed.length < 2 ? (
        <p className="text-xs text-[color:var(--color-muted-fg)]">
          Type at least 2 characters to search.
        </p>
      ) : searchQuery.isLoading ? (
        <div className="flex items-center gap-2 text-sm text-[color:var(--color-muted-fg)]">
          <Spinner size={16} /> Searching…
        </div>
      ) : results.length === 0 ? (
        <p className="text-xs text-[color:var(--color-muted-fg)]">No matches.</p>
      ) : (
        <ul className="divide-y divide-[color:var(--color-border)] rounded-md border border-[color:var(--color-border)] overflow-hidden">
          {results.map((r) => (
            <li key={r.novaradPatientId}>
              <button
                type="button"
                onClick={() => onPick(r)}
                className="w-full text-left px-3 py-2 hover:bg-[color:var(--color-surface-2)]/50"
              >
                <div className="text-sm font-medium">
                  {fullName(r.lastName, r.firstName)}
                </div>
                <div className="text-xs font-mono text-[color:var(--color-muted-fg)]">
                  PID {r.pid ?? "—"} · DOB {formatDate(r.birthDate)} ·{" "}
                  {r.gender ?? "—"}
                </div>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function Group({
  icon,
  title,
  children,
}: {
  icon: React.ReactNode;
  title: string;
  children: React.ReactNode;
}) {
  return (
    <div className="rounded-md border border-[color:var(--color-border)] p-4 bg-[color:var(--color-surface-2)]/40">
      <div className="flex items-center gap-2 text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)] mb-3">
        {icon}
        {title}
      </div>
      <dl className="grid grid-cols-1 sm:grid-cols-2 gap-y-3 gap-x-6 text-sm">
        {children}
      </dl>
    </div>
  );
}

function Cell({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <dt className="text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)]">
        {label}
      </dt>
      <dd className="mt-0.5">{children}</dd>
    </div>
  );
}
