"use client";

import { useQuery } from "@tanstack/react-query";
import { useMemo } from "react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardBody, CardHeader, CardSubtitle, CardTitle } from "@/components/ui/card";
import { Spinner } from "@/components/ui/spinner";
import { techApi } from "@/lib/api";
import type { PatientJacketEntry } from "@/lib/types";
import { formatDate } from "@/lib/utils";

export function StepComparisons({
  novaradPatientId,
  currentStudyId,
  currentDescription,
  currentModality,
  selectedIds,
  onChange,
  onBack,
  onContinue,
  saving,
}: {
  novaradPatientId: number | null;
  currentStudyId: number;
  currentDescription: string | null;
  currentModality: string | null;
  selectedIds: number[];
  onChange: (ids: number[]) => void;
  onBack: () => void;
  onContinue: () => void;
  saving: boolean;
}) {
  const jacketQuery = useQuery({
    queryKey: ["jacket", novaradPatientId, currentStudyId, currentDescription, currentModality],
    queryFn: () =>
      techApi.patientJacket(novaradPatientId!, {
        currentStudyId,
        description: currentDescription,
        modality: currentModality,
      }),
    enabled: novaradPatientId != null,
  });

  const selected = useMemo(() => new Set(selectedIds), [selectedIds]);
  const entries = jacketQuery.data ?? [];
  const suggested = useMemo(() => entries.filter((e) => e.suggested), [entries]);
  const others = useMemo(
    () =>
      entries
        .filter((e) => !e.suggested)
        .sort((a, b) => (b.studyDate ?? "").localeCompare(a.studyDate ?? "")),
    [entries],
  );

  function toggle(id: number) {
    const next = new Set(selected);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    onChange(Array.from(next));
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Flag comparison studies</CardTitle>
        <CardSubtitle>
          Pick any prior studies the radiologist should compare against. Suggested
          priors (same modality, similar description) are pinned at the top. This
          step is optional — Continue if there are no relevant priors.
        </CardSubtitle>
      </CardHeader>
      <CardBody className="space-y-4">
        {jacketQuery.isLoading ? (
          <div className="flex items-center justify-center py-10">
            <Spinner size={20} />
          </div>
        ) : entries.length === 0 ? (
          <div className="text-sm text-[color:var(--color-muted-fg)] py-6 text-center">
            No priors found for this patient.
          </div>
        ) : (
          <>
            {suggested.length > 0 ? (
              <section className="space-y-2">
                <div className="text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-accent)]">
                  Suggested comparisons
                </div>
                <JacketList
                  rows={suggested}
                  selected={selected}
                  onToggle={toggle}
                  showSuggestedBadge
                />
              </section>
            ) : null}

            {others.length > 0 ? (
              <section className="space-y-2">
                <div className="text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)]">
                  All priors
                </div>
                <JacketList rows={others} selected={selected} onToggle={toggle} />
              </section>
            ) : null}
          </>
        )}
      </CardBody>
      <div className="px-5 py-3 border-t border-[color:var(--color-border)] flex items-center justify-between">
        <Button variant="ghost" onClick={onBack}>
          Back
        </Button>
        <Button onClick={onContinue} loading={saving}>
          Continue
        </Button>
      </div>
    </Card>
  );
}

function JacketList({
  rows,
  selected,
  onToggle,
  showSuggestedBadge = false,
}: {
  rows: PatientJacketEntry[];
  selected: Set<number>;
  onToggle: (id: number) => void;
  showSuggestedBadge?: boolean;
}) {
  return (
    <ul className="space-y-1">
      {rows.map((r) => (
        <li key={r.novaradStudyId}>
          <label className="flex items-start gap-3 rounded p-2 cursor-pointer hover:bg-[color:var(--color-surface-2)]">
            <input
              type="checkbox"
              className="mt-1"
              checked={selected.has(r.novaradStudyId)}
              onChange={() => onToggle(r.novaradStudyId)}
            />
            <div className="text-xs flex-1 min-w-0">
              <div className="font-medium flex items-center gap-2 flex-wrap">
                <span>{formatDate(r.studyDate)}</span>
                <span className="text-[color:var(--color-muted-fg)]">·</span>
                <span>{r.modality ?? "—"}</span>
                <span className="text-[color:var(--color-muted-fg)]">·</span>
                <span className="truncate">{r.description ?? "—"}</span>
                {showSuggestedBadge ? (
                  <Badge variant="accent" className="text-[10px]">
                    suggested
                  </Badge>
                ) : null}
              </div>
              <div className="text-[color:var(--color-muted-fg)] font-mono mt-0.5">
                Accession {r.accession ?? "—"}
              </div>
            </div>
          </label>
        </li>
      ))}
    </ul>
  );
}
