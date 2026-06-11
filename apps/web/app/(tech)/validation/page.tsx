"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useVirtualizer } from "@tanstack/react-virtual";
import { AxiosError } from "axios";
import {
  AlertCircle,
  Check,
  ChevronDown,
  ChevronRight,
  Copy,
  Merge,
  RefreshCw,
  Search,
  X,
} from "lucide-react";
import { useRouter } from "next/navigation";
import {
  memo,
  startTransition,
  useCallback,
  useMemo,
  useRef,
  useState,
} from "react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog, DialogActions } from "@/components/ui/dialog";
import { Input, Label, Textarea } from "@/components/ui/input";
import { Spinner } from "@/components/ui/spinner";
import { techApi } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import {
  isNrs,
  type ReadyStudy,
  type StudyMergeOutcome,
} from "@/lib/types";
import { cn, computeAge, formatDate, fullName } from "@/lib/utils";

interface PatientGroup {
  patientId: number;
  studies: ReadyStudy[];
  // Accessions that appear on more than one study within the group. When non-empty
  // the per-child row gets a "duplicate accession" badge and the merge dialog
  // defaults its loser-selection to the duplicate set with the newest study as
  // keeper. Empty for groups whose studies all have distinct accessions.
  duplicateAccessions: Set<string>;
}

// Virtualized-list item shape. The worklist's visible content is a flat array
// of these — every singleton, every group header, and every visible group
// child becomes one item with its own index in the virtualizer.
type VirtualItem =
  | { kind: "singleton"; key: string; study: ReadyStudy }
  | { kind: "groupHeader"; key: string; group: PatientGroup; expanded: boolean }
  | { kind: "groupChild"; key: string; study: ReadyStudy; duplicateAccession: boolean };

function flattenGroups(
  groups: PatientGroup[],
  expanded: Set<number>,
): VirtualItem[] {
  const items: VirtualItem[] = [];
  for (const g of groups) {
    if (g.studies.length === 1) {
      const s = g.studies[0]!;
      items.push({ kind: "singleton", key: `s-${s.novaradStudyId}`, study: s });
    } else {
      const isExp = expanded.has(g.patientId);
      items.push({
        kind: "groupHeader",
        key: `gh-${g.patientId}`,
        group: g,
        expanded: isExp,
      });
      if (isExp) {
        for (const s of g.studies) {
          items.push({
            kind: "groupChild",
            key: `gc-${s.novaradStudyId}`,
            study: s,
            duplicateAccession: s.accession != null && g.duplicateAccessions.has(s.accession),
          });
        }
      }
    }
  }
  return items;
}

// Shared grid template — both the header row and every body row use this so
// columns line up under virtualization (no <table> auto-layout to lean on).
// chevron | PID | patient (flex) | DOB | Accession | Modality | Study date | Status
const GRID_COLS =
  "[grid-template-columns:2rem_8rem_minmax(14rem,1fr)_8rem_9rem_5rem_7rem_14rem]";

function groupByPatient(rows: ReadyStudy[]): PatientGroup[] {
  const map = new Map<number, ReadyStudy[]>();
  for (const s of rows) {
    const arr = map.get(s.novaradPatientId);
    if (arr) arr.push(s);
    else map.set(s.novaradPatientId, [s]);
  }
  // Stable presentation: original row order is already newest-first per the
  // worklist projection; preserve it by keying off the first study seen.
  const result: PatientGroup[] = [];
  const seen = new Set<number>();
  for (const s of rows) {
    if (seen.has(s.novaradPatientId)) continue;
    seen.add(s.novaradPatientId);
    const studies = map.get(s.novaradPatientId)!;
    // An accession is "duplicated" inside a group iff two or more studies in the
    // group carry the same non-null accession — almost always a tech reshooting
    // the same study without canceling the first one.
    const accessionCounts = new Map<string, number>();
    for (const study of studies) {
      if (study.accession == null) continue;
      accessionCounts.set(study.accession, (accessionCounts.get(study.accession) ?? 0) + 1);
    }
    const duplicateAccessions = new Set<string>();
    for (const [acc, count] of accessionCounts) {
      if (count >= 2) duplicateAccessions.add(acc);
    }
    result.push({
      patientId: s.novaradPatientId,
      studies,
      duplicateAccessions,
    });
  }
  return result;
}

// Render: "2 share accession 233923620250217 — probably duplicates." for one
// duplicated accession, or "Two accessions are duplicated across these studies."
// when more than one set repeats. Singular/plural friendly without a templating dance.
function duplicateAccessionsSummary(group: PatientGroup): string {
  const dups = Array.from(group.duplicateAccessions);
  if (dups.length === 0) return "";
  if (dups.length === 1) {
    const acc = dups[0]!;
    const count = group.studies.filter((s) => s.accession === acc).length;
    return `${count} share accession ${acc} — probably duplicates.`;
  }
  return `${dups.length} accessions are duplicated across these studies — probably duplicates.`;
}

const WORKLIST_LIMIT = 500;
const REFRESH_MS_KEY = "worklist-refresh-ms";
const REFRESH_ON_KEY = "worklist-refresh-on";
const REFRESH_OPTIONS: { label: string; ms: number }[] = [
  { label: "15s", ms: 15_000 },
  { label: "30s", ms: 30_000 },
  { label: "1m", ms: 60_000 },
  { label: "5m", ms: 300_000 },
];

function matchesSearch(s: ReadyStudy, q: string): boolean {
  const hay = [
    s.patientLastName,
    s.patientFirstName,
    s.patientPid,
    s.accession,
    s.modality,
    s.studyUid,
  ]
    .filter(Boolean)
    .join(" ")
    .toLowerCase();
  return hay.includes(q);
}

const SHOW_DEV_TOOLS = process.env.NEXT_PUBLIC_SHOW_DEV_TOOLS === "true";

export default function WorklistPage() {
  const router = useRouter();
  const queryClient = useQueryClient();
  const { user } = useAuth();
  const [refreshError, setRefreshError] = useState<string | null>(null);
  const [search, setSearch] = useState("");

  // Auto-refresh on/off + cadence, both remembered per-browser. The toggle lets a
  // user stop the polling entirely; the interval only applies while it's on.
  const [refreshOn, setRefreshOn] = useState<boolean>(() => {
    if (typeof window === "undefined") return true;
    return window.localStorage.getItem(REFRESH_ON_KEY) !== "false";
  });
  const [refreshMs, setRefreshMs] = useState<number>(() => {
    if (typeof window === "undefined") return 30_000;
    const saved = window.localStorage.getItem(REFRESH_MS_KEY);
    return saved !== null ? Number(saved) : 30_000;
  });
  function toggleRefresh(on: boolean) {
    setRefreshOn(on);
    if (typeof window !== "undefined") {
      window.localStorage.setItem(REFRESH_ON_KEY, String(on));
    }
  }
  function changeRefresh(ms: number) {
    setRefreshMs(ms);
    if (typeof window !== "undefined") {
      window.localStorage.setItem(REFRESH_MS_KEY, String(ms));
    }
  }

  const worklist = useQuery({
    queryKey: ["worklist"],
    queryFn: () => techApi.worklist({ limit: WORKLIST_LIMIT }),
    refetchInterval: refreshOn && refreshMs > 0 ? refreshMs : false,
  });

  const startMutation = useMutation({
    mutationFn: (study: ReadyStudy) =>
      techApi.start({
        novaradStudyId: study.novaradStudyId,
        novaradPatientId: study.novaradPatientId,
      }),
    onSuccess: (record) => {
      queryClient.invalidateQueries({ queryKey: ["worklist"] });
      router.push(`/validation/wizard/${record.validationId}`);
    },
  });

  const refreshMutation = useMutation({
    mutationFn: () => techApi.devRefresh(),
    onSuccess: () => {
      setRefreshError(null);
      queryClient.invalidateQueries({ queryKey: ["worklist"] });
    },
    onError: (err) => {
      const ax = err as AxiosError<{ message?: string }>;
      setRefreshError(
        ax.response?.data?.message ??
          (ax.response?.status === 403
            ? "Refresh is NRS-only."
            : "Refresh failed."),
      );
    },
  });

  const rows = worklist.data ?? [];
  const query = search.trim().toLowerCase();
  const filtered = useMemo(
    () => (query ? rows.filter((s) => matchesSearch(s, query)) : rows),
    [rows, query],
  );
  const groups = useMemo(() => groupByPatient(filtered), [filtered]);
  const multiPatientCount = useMemo(
    () => groups.filter((g) => g.studies.length > 1).length,
    [groups],
  );

  // Collapsed by default so the worklist stays compact; the merge-candidate
  // banner advertises that there's more to see.
  const [expandedPatients, setExpandedPatients] = useState<Set<number>>(
    () => new Set(),
  );
  // Stable handlers + memo'd row components so toggling one group's chevron
  // doesn't re-render the other 500+ worklist rows. The toggle itself runs
  // inside startTransition: the React.memo reconciliation is cheap, but
  // inserting/removing N child <tr> nodes inside a 500-row table forces a
  // table-wide layout reflow. startTransition tells React the expansion is
  // non-urgent so the click + subsequent scroll stay responsive while the
  // layout work happens in the background.
  const handleToggleGroup = useCallback((patientId: number) => {
    startTransition(() => {
      setExpandedPatients((prev) => {
        const next = new Set(prev);
        if (next.has(patientId)) next.delete(patientId);
        else next.add(patientId);
        return next;
      });
    });
  }, []);

  const startMutate = startMutation.mutate;
  const handlePick = useCallback(
    (study: ReadyStudy) => startMutate(study),
    [startMutate],
  );

  const [mergeTarget, setMergeTarget] = useState<PatientGroup | null>(null);
  const handleMerge = useCallback(
    (group: PatientGroup) => setMergeTarget(group),
    [],
  );

  // Virtualization. Singletons + group headers + (when expanded) group
  // children become items in a flat list. The virtualizer only renders the
  // ~12-15 rows currently on screen, so chevron toggles don't reflow 500
  // DOM nodes anymore.
  const virtualItems = useMemo(
    () => flattenGroups(groups, expandedPatients),
    [groups, expandedPatients],
  );
  const scrollParentRef = useRef<HTMLDivElement>(null);
  const rowVirtualizer = useVirtualizer({
    count: virtualItems.length,
    getScrollElement: () => scrollParentRef.current,
    // Initial guess — measureElement upgrades to the real height once each
    // row has mounted, which handles the chevron-expanded group header that
    // ends up a bit taller than a plain row.
    estimateSize: () => 60,
    overscan: 8,
    getItemKey: (index) => virtualItems[index]!.key,
  });

  const mergeMutation = useMutation({
    mutationFn: (req: { winningStudyId: number; losingStudyIds: number[]; reason: string }) =>
      techApi.mergeStudies({
        winningStudyId: req.winningStudyId,
        losingStudyIds: req.losingStudyIds,
        reason: req.reason || null,
      }),
    onSuccess: (outcome) => {
      queryClient.invalidateQueries({ queryKey: ["worklist"] });
      // Close on full success; on partial failure the dialog stays open so the
      // tech can see which studies didn't merge.
      if (outcome.preflightError == null && outcome.failedCount === 0) {
        setMergeTarget(null);
      }
    },
  });

  return (
    <div className="mx-auto max-w-7xl px-6 py-8">
      <div className="flex flex-wrap items-end justify-between gap-4 mb-6">
        <div>
          <p className="text-[10px] uppercase tracking-[0.3em] text-[color:var(--color-accent)]">
            Tech Validation
          </p>
          <h1
            className="text-3xl mt-1"
            style={{ fontFamily: "var(--font-display)" }}
          >
            Ready Studies
          </h1>
          <p className="text-sm text-[color:var(--color-muted-fg)] mt-1">
            Studies waiting for tech validation.
          </p>
        </div>
        <div className="flex items-center gap-2">
          {worklist.isFetching ? <Spinner size={16} /> : null}
          <div className="flex items-center gap-2 text-xs text-[color:var(--color-muted-fg)]">
            <button
              type="button"
              role="switch"
              aria-checked={refreshOn}
              aria-label="Toggle auto-refresh"
              onClick={() => toggleRefresh(!refreshOn)}
              className={cn(
                "relative inline-flex h-5 w-9 items-center rounded-full transition-colors",
                refreshOn
                  ? "bg-[color:var(--color-accent)]"
                  : "bg-[color:var(--color-border)]",
              )}
            >
              <span
                className={cn(
                  "inline-block h-4 w-4 rounded-full bg-white shadow transition-transform",
                  refreshOn ? "translate-x-4" : "translate-x-0.5",
                )}
              />
            </button>
            <span>Auto-refresh</span>
            {refreshOn ? (
              <select
                value={refreshMs}
                onChange={(e) => changeRefresh(Number(e.target.value))}
                aria-label="Auto-refresh interval"
                className="h-8 rounded-md border border-[color:var(--color-border)] bg-[color:var(--color-surface)] px-2 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-accent)]/60"
              >
                {REFRESH_OPTIONS.map((o) => (
                  <option key={o.ms} value={o.ms}>
                    {o.label}
                  </option>
                ))}
              </select>
            ) : null}
          </div>
          <Button
            variant="secondary"
            size="sm"
            onClick={() => worklist.refetch()}
            disabled={worklist.isFetching}
          >
            <RefreshCw className="size-4" />
            Refresh view
          </Button>
          {SHOW_DEV_TOOLS && user && isNrs(user.role) ? (
            <Button
              variant="primary"
              size="sm"
              onClick={() => refreshMutation.mutate()}
              loading={refreshMutation.isPending}
              title="NRS-only dev helper — trigger ReadyStudiesProjector"
            >
              Project now
            </Button>
          ) : null}
        </div>
      </div>

      {refreshError ? (
        <div className="mb-4 rounded-md border border-[color:var(--color-novarad-red)]/40 bg-[color:var(--color-novarad-red)]/10 px-3 py-2 text-sm text-[color:var(--color-novarad-red)] flex items-center gap-2">
          <AlertCircle className="size-4" />
          {refreshError}
        </div>
      ) : null}

      {worklist.isError ? (
        <div className="rounded-md border border-[color:var(--color-novarad-red)]/40 bg-[color:var(--color-novarad-red)]/10 px-3 py-3 text-sm text-[color:var(--color-novarad-red)] flex items-center gap-2">
          <AlertCircle className="size-4" />
          Couldn&apos;t load the worklist.{" "}
          <button
            className="underline underline-offset-2"
            onClick={() => worklist.refetch()}
          >
            Try again
          </button>
          .
        </div>
      ) : null}

      <div className="mb-3 flex items-center gap-3">
        <div className="relative flex-1 max-w-md">
          <Search className="absolute left-3 top-1/2 -translate-y-1/2 size-4 text-[color:var(--color-muted-fg)]" />
          <Input
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder="Search name, PID, accession, modality…"
            aria-label="Search ready studies"
            className="pl-9 pr-9"
          />
          {search ? (
            <button
              type="button"
              onClick={() => setSearch("")}
              aria-label="Clear search"
              className="absolute right-2 top-1/2 -translate-y-1/2 text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-base-fg)]"
            >
              <X className="size-4" />
            </button>
          ) : null}
        </div>
        <span className="ml-auto text-xs text-[color:var(--color-muted-fg)] tabular-nums">
          {worklist.isLoading
            ? null
            : query
              ? `${filtered.length} of ${rows.length} studies`
              : `${rows.length} stud${rows.length === 1 ? "y" : "ies"}${rows.length === WORKLIST_LIMIT ? "+" : ""}`}
        </span>
      </div>

      {multiPatientCount > 0 ? (
        <div className="mb-3 rounded-md border border-[color:var(--color-accent)]/40 bg-[color:var(--color-accent)]/10 px-3 py-2 text-xs text-[color:var(--color-base-fg)] flex items-center gap-2">
          <Merge className="size-3.5 text-[color:var(--color-accent)]" />
          <span>
            <span className="font-mono tabular-nums">{multiPatientCount}</span>{" "}
            patient{multiPatientCount === 1 ? " has" : "s have"} more than one
            new study. Expand the row to merge them.
          </span>
        </div>
      ) : null}

      <div
        role="table"
        aria-rowcount={virtualItems.length}
        className="rounded-lg border border-[color:var(--color-border)] bg-[color:var(--color-surface)] overflow-hidden"
      >
        {/* Header row — sits outside the virtualized scroll container so it
            stays put while the rows scroll. */}
        <div
          role="row"
          className={cn(
            "grid items-center text-left text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)] bg-[color:var(--color-surface-2)] border-b border-[color:var(--color-border)]",
            GRID_COLS,
          )}
        >
          <span role="columnheader" aria-label="Expand" className="px-2 py-3" />
          <span role="columnheader" className="px-4 py-3 font-medium">PID</span>
          <span role="columnheader" className="px-4 py-3 font-medium">Patient</span>
          <span role="columnheader" className="px-4 py-3 font-medium">DOB · Age</span>
          <span role="columnheader" className="px-4 py-3 font-medium">Accession</span>
          <span role="columnheader" className="px-4 py-3 font-medium">Modality</span>
          <span role="columnheader" className="px-4 py-3 font-medium">Study date</span>
          <span role="columnheader" className="px-4 py-3 font-medium">Status</span>
        </div>

        {worklist.isLoading ? (
          <div className="px-4 py-10 text-center text-[color:var(--color-muted-fg)]">
            <Spinner size={20} />
          </div>
        ) : rows.length === 0 && !worklist.isError ? (
          <div className="px-4 py-10 text-center text-sm text-[color:var(--color-muted-fg)]">
            No ready studies right now. Newly tagged studies appear here within ~60 seconds.
          </div>
        ) : filtered.length === 0 ? (
          <div className="px-4 py-10 text-center text-sm text-[color:var(--color-muted-fg)]">
            No studies match &ldquo;{search.trim()}&rdquo;.
          </div>
        ) : (
          // Bounded scroll container so the virtualizer has a fixed-height
          // viewport to measure against. The contain hint scopes layout
          // invalidation to this subtree on every chevron toggle.
          <div
            ref={scrollParentRef}
            role="rowgroup"
            className="overflow-y-auto [contain:layout_paint] [max-height:calc(100vh-280px)] [min-height:24rem]"
          >
            <div
              style={{
                height: `${rowVirtualizer.getTotalSize()}px`,
                position: "relative",
              }}
            >
              {rowVirtualizer.getVirtualItems().map((vi) => {
                const item = virtualItems[vi.index]!;
                return (
                  <div
                    key={item.key}
                    data-index={vi.index}
                    ref={rowVirtualizer.measureElement}
                    style={{
                      position: "absolute",
                      top: 0,
                      left: 0,
                      width: "100%",
                      transform: `translateY(${vi.start}px)`,
                    }}
                  >
                    {item.kind === "singleton" ? (
                      <WorklistRow
                        study={item.study}
                        disabled={startMutation.isPending}
                        onPick={handlePick}
                      />
                    ) : item.kind === "groupHeader" ? (
                      <GroupHeaderRow
                        group={item.group}
                        expanded={item.expanded}
                        onToggle={handleToggleGroup}
                        onMerge={handleMerge}
                        disabled={startMutation.isPending || mergeMutation.isPending}
                      />
                    ) : (
                      <WorklistRow
                        study={item.study}
                        disabled={startMutation.isPending}
                        onPick={handlePick}
                        indent
                        duplicateAccession={item.duplicateAccession}
                      />
                    )}
                  </div>
                );
              })}
            </div>
          </div>
        )}
      </div>

      {startMutation.isError ? (
        <p className="mt-3 text-sm text-[color:var(--color-novarad-red)]">
          Couldn&apos;t start the wizard. Try again.
        </p>
      ) : null}

      <MergeStudiesDialog
        key={mergeTarget?.patientId ?? "closed"}
        target={mergeTarget}
        onClose={() => {
          setMergeTarget(null);
          mergeMutation.reset();
        }}
        onSubmit={(winningStudyId, losingStudyIds, reason) =>
          mergeMutation.mutate({
            winningStudyId,
            losingStudyIds,
            reason,
          })
        }
        submitting={mergeMutation.isPending}
        outcome={mergeMutation.data ?? null}
        errorMessage={mergeMutation.isError ? "Couldn't reach the merge endpoint." : null}
      />
    </div>
  );
}

const WorklistRow = memo(function WorklistRow({
  study,
  disabled,
  onPick,
  indent = false,
  duplicateAccession = false,
}: {
  study: ReadyStudy;
  disabled: boolean;
  onPick: (study: ReadyStudy) => void;
  indent?: boolean;
  duplicateAccession?: boolean;
}) {
  const claimed = !!study.inProgressValidationId;

  return (
    <div
      role="row"
      className={cn(
        "grid items-center text-sm border-t border-[color:var(--color-border)]",
        GRID_COLS,
        // Group-child rows: a left accent stripe + faint accent tint anchors them to
        // their parent and separates them from a singleton row immediately below.
        indent &&
          "bg-[color:var(--color-accent)]/4 border-l-2 border-l-[color:var(--color-accent)]/50",
        // Claimed rows use a slightly stronger accent tint than group-children so they
        // remain distinguishable at a glance. Never `--color-caution` — red implies an
        // error, but "in progress" is just informational.
        claimed && "bg-[color:var(--color-accent)]/10",
        disabled && "opacity-60 pointer-events-none",
      )}
    >
      <span role="cell" className="px-2 py-3" />
      <span role="cell" className={cn("px-4 py-3 font-mono text-xs", indent && "pl-10")}>
        {study.patientPid ? (
          <CopyableInline text={study.patientPid} mono />
        ) : (
          <span className="text-[color:var(--color-muted-fg)]">—</span>
        )}
      </span>
      <span role="cell" className="px-4 py-3 min-w-0">
        <div className="font-medium truncate">
          {fullName(study.patientLastName, study.patientFirstName)}
        </div>
        <div className="text-xs text-[color:var(--color-muted-fg)]">
          {study.studyDescription ? (
            <CopyableInline text={study.studyDescription} />
          ) : (
            "—"
          )}
        </div>
      </span>
      <span role="cell" className="px-4 py-3">
        <div>{formatDate(study.patientBirthDate)}</div>
        <div className="text-xs text-[color:var(--color-muted-fg)]">
          {computeAge(study.patientBirthDate)}
        </div>
      </span>
      <span role="cell" className="px-4 py-3 font-mono text-xs min-w-0">
        {study.accession ? (
          <CopyableInline text={study.accession} mono />
        ) : (
          <span className="text-[color:var(--color-muted-fg)]">—</span>
        )}
        {duplicateAccession ? (
          <Badge variant="accent" className="mt-1 font-sans text-[10px]" title="Another study in this group shares this accession">
            duplicate accession
          </Badge>
        ) : null}
      </span>
      <span role="cell" className="px-4 py-3">{study.modality ?? "—"}</span>
      <span role="cell" className="px-4 py-3">{formatDate(study.studyDate)}</span>
      <span role="cell" className="px-4 py-3">
        {claimed ? (
          <div className="flex items-center gap-2 flex-wrap">
            <Badge variant="accent">
              In progress · {study.inProgressStartedByDisplay ?? "someone"}
            </Badge>
            <Button
              variant="secondary"
              size="sm"
              onClick={() => onPick(study)}
              disabled={disabled}
              className="h-7 px-2 text-xs"
            >
              Resume
            </Button>
          </div>
        ) : (
          <Button
            variant="primary"
            size="sm"
            onClick={() => onPick(study)}
            disabled={disabled}
            className="h-8"
          >
            Validate
          </Button>
        )}
      </span>
    </div>
  );
});

/** Inline value with a tiny clipboard-copy affordance to its right. Used for IDs
 *  and descriptions in the worklist so highlighting text doesn't fight with a
 *  whole-row click handler. */
function CopyableInline({ text, mono = false }: { text: string; mono?: boolean }) {
  const [copied, setCopied] = useState(false);
  async function copy(e: React.MouseEvent) {
    e.stopPropagation();
    try {
      await navigator.clipboard.writeText(text);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1200);
    } catch {
      // Older browsers / insecure contexts: silently fail; the user can still
      // highlight + Ctrl+C since the row click handler is gone.
    }
  }
  return (
    <span className="inline-flex items-center gap-1 min-w-0">
      <span className={cn("truncate", mono && "font-mono")}>{text}</span>
      <button
        type="button"
        onClick={copy}
        title={copied ? "Copied" : "Copy"}
        aria-label={copied ? "Copied" : "Copy to clipboard"}
        className="shrink-0 inline-flex items-center justify-center rounded p-0.5 text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-base-fg)] hover:bg-[color:var(--color-surface-2)] opacity-60 hover:opacity-100"
      >
        {copied ? <Check className="size-3" /> : <Copy className="size-3" />}
      </button>
    </span>
  );
}

const GroupHeaderRow = memo(function GroupHeaderRow({
  group,
  expanded,
  onToggle,
  onMerge,
  disabled,
}: {
  group: PatientGroup;
  expanded: boolean;
  onToggle: (patientId: number) => void;
  onMerge: (group: PatientGroup) => void;
  disabled: boolean;
}) {
  const head = group.studies[0]!;
  const Chevron = expanded ? ChevronDown : ChevronRight;
  return (
    <div
      role="row"
      className={cn(
        "grid items-center text-sm border-t border-[color:var(--color-border)] bg-[color:var(--color-accent)]/10 hover:bg-[color:var(--color-accent)]/15",
        GRID_COLS,
      )}
    >
      <span role="cell" className="px-2 py-3">
        <button
          type="button"
          aria-label={expanded ? "Collapse" : "Expand"}
          aria-expanded={expanded}
          onClick={() => onToggle(group.patientId)}
          className="inline-flex items-center justify-center rounded p-1 hover:bg-[color:var(--color-surface-2)]"
        >
          <Chevron className="size-4 text-[color:var(--color-muted-fg)]" />
        </button>
      </span>
      <span role="cell" className="px-4 py-3 font-mono text-xs">
        {head.patientPid ?? <span className="text-[color:var(--color-muted-fg)]">—</span>}
      </span>
      <span role="cell" className="px-4 py-3 min-w-0">
        <div className="font-medium flex items-center gap-2 truncate">
          <span className="truncate">{fullName(head.patientLastName, head.patientFirstName)}</span>
          <Badge variant="accent" title="More than one new study for this patient">
            <Merge className="size-3 mr-1" />
            {group.studies.length} studies
          </Badge>
        </div>
      </span>
      <span role="cell" className="px-4 py-3">
        <div>{formatDate(head.patientBirthDate)}</div>
        <div className="text-xs text-[color:var(--color-muted-fg)]">
          {computeAge(head.patientBirthDate)}
        </div>
      </span>
      <span
        role="cell"
        className="px-4 py-3 text-xs text-[color:var(--color-muted-fg)] [grid-column:span_3]"
      >
        <div>Looks like these studies are for the same patient. You can merge them into one.</div>
        {group.duplicateAccessions.size > 0 ? (
          <div className="mt-0.5 text-[color:var(--color-accent)]">
            {duplicateAccessionsSummary(group)}
          </div>
        ) : null}
      </span>
      <span role="cell" className="px-4 py-3 whitespace-nowrap">
        <Button
          variant="secondary"
          size="sm"
          onClick={() => onMerge(group)}
          disabled={disabled}
          title="Merge these studies into a single study"
          className="whitespace-nowrap"
        >
          <Merge className="size-4" />
          Merge Studies
        </Button>
      </span>
    </div>
  );
});

function MergeStudiesDialog({
  target,
  onClose,
  onSubmit,
  submitting,
  outcome,
  errorMessage,
}: {
  target: PatientGroup | null;
  onClose: () => void;
  onSubmit: (winningStudyId: number, losingStudyIds: number[], reason: string) => void;
  submitting: boolean;
  outcome: StudyMergeOutcome | null;
  errorMessage: string | null;
}) {
  const open = target != null;
  // Caller passes a `key` derived from target.patientId so this component
  // remounts per merge target — useState initializers re-run against the new
  // group and we don't need imperative state syncing.
  // When the group has a duplicate-accession set, bias the dialog toward merging
  // just that pair (newest study as keeper, the other duplicates as losers) — the
  // most common reason to open this dialog is exactly that case. Tech can still
  // adjust. Otherwise fall back to "first study is keeper, all others are losers".
  const studiesForInit = target?.studies ?? [];
  const duplicateStudies = studiesForInit.filter(
    (s) => s.accession != null && target?.duplicateAccessions.has(s.accession),
  );
  let initialKeeperId: number | undefined;
  let initialLosers: number[];
  if (duplicateStudies.length >= 2) {
    const sorted = [...duplicateStudies].sort((a, b) => {
      const ad = a.studyDate ?? "";
      const bd = b.studyDate ?? "";
      return bd.localeCompare(ad);
    });
    initialKeeperId = sorted[0]!.novaradStudyId;
    initialLosers = sorted.slice(1).map((s) => s.novaradStudyId);
  } else {
    initialKeeperId = studiesForInit[0]?.novaradStudyId;
    initialLosers = studiesForInit
      .map((s) => s.novaradStudyId)
      .filter((id) => id !== initialKeeperId);
  }
  const [keeperId, setKeeperId] = useState<number | undefined>(() => initialKeeperId);
  const [losingSelections, setLosingSelections] = useState<Set<number>>(
    () => new Set(initialLosers),
  );
  const [reason, setReason] = useState("");

  const studies = target?.studies ?? [];
  const head = studies[0];

  const selectedCount = losingSelections.size;
  const patientName = head ? fullName(head.patientLastName, head.patientFirstName) : "";

  // When the tech picks a different keeper, fold the previous keeper into the loser
  // selection (auto-checked, matching default behavior) and remove the new keeper.
  function pickKeeper(id: number) {
    if (id === keeperId) return;
    setLosingSelections((prev) => {
      const next = new Set(prev);
      next.delete(id);
      if (keeperId != null) next.add(keeperId);
      return next;
    });
    setKeeperId(id);
  }

  function toggleLoser(id: number, checked: boolean) {
    setLosingSelections((prev) => {
      const next = new Set(prev);
      if (checked) next.add(id);
      else next.delete(id);
      return next;
    });
  }

  return (
    <Dialog
      open={open}
      onClose={() => {
        if (!submitting) onClose();
      }}
      title="Merge studies into one"
      description={
        head
          ? `The following ${studies.length} studies belong to ${patientName}. Please select the main study. The others will be combined with it.`
          : undefined
      }
    >
      <div className="space-y-3">
        <Label>Select the main study</Label>
        <div className="space-y-2 max-h-64 overflow-y-auto rounded-md border border-[color:var(--color-border)] p-2">
          {studies.map((s) => {
            const isKeeper = keeperId === s.novaradStudyId;
            const isSelected = losingSelections.has(s.novaradStudyId);
            return (
              <div
                key={s.novaradStudyId}
                className={cn(
                  "flex items-start gap-3 rounded p-2 hover:bg-[color:var(--color-surface-2)]",
                  isKeeper && "bg-[color:var(--color-accent)]/10",
                )}
              >
                <label className="flex items-center gap-2 cursor-pointer pt-0.5">
                  <input
                    type="radio"
                    name="keeper"
                    value={s.novaradStudyId}
                    checked={isKeeper}
                    onChange={() => pickKeeper(s.novaradStudyId)}
                    aria-label="Main study"
                  />
                  <span className="text-[10px] uppercase tracking-wide text-[color:var(--color-muted-fg)]">
                    Main
                  </span>
                </label>
                <label className="flex items-center gap-2 cursor-pointer pt-0.5">
                  <input
                    type="checkbox"
                    checked={!isKeeper && isSelected}
                    disabled={isKeeper}
                    onChange={(e) => toggleLoser(s.novaradStudyId, e.target.checked)}
                    aria-label="Merge this study into main"
                  />
                  <span className="text-[10px] uppercase tracking-wide text-[color:var(--color-muted-fg)]">
                    Merge
                  </span>
                </label>
                <div className="text-xs">
                  <div className="font-medium">
                    {s.modality ?? "—"} · Accession {s.accession ?? "—"}
                  </div>
                  <div className="text-[color:var(--color-muted-fg)]">
                    Study date {formatDate(s.studyDate)}
                  </div>
                </div>
              </div>
            );
          })}
        </div>

        <Label>Note (optional)</Label>
        <Textarea
          value={reason}
          onChange={(e) => setReason(e.target.value)}
          rows={2}
          placeholder="Anything to capture about why these are being combined"
        />

        {outcome ? (
          <div
            className={cn(
              "rounded-md border px-3 py-2 text-xs",
              outcome.preflightError
                ? "border-[color:var(--color-novarad-red)]/40 bg-[color:var(--color-novarad-red)]/10 text-[color:var(--color-novarad-red)]"
                : outcome.failedCount > 0
                  ? "border-[color:var(--color-novarad-red)]/40 bg-[color:var(--color-novarad-red)]/10 text-[color:var(--color-novarad-red)]"
                  : "border-[color:var(--color-accent)]/40 bg-[color:var(--color-accent)]/10",
            )}
          >
            {outcome.preflightError ? (
              <>Couldn&apos;t merge these studies: {outcome.preflightError}</>
            ) : (
              <>
                Merged {outcome.mergedCount} stud
                {outcome.mergedCount === 1 ? "y" : "ies"} into the main study.
                {outcome.failedCount > 0
                  ? ` ${outcome.failedCount} didn't go through.`
                  : ""}
              </>
            )}
            {outcome.rows.filter((r) => !r.success).length > 0 ? (
              <ul className="mt-1 space-y-0.5">
                {outcome.rows
                  .filter((r) => !r.success)
                  .map((r) => (
                    <li key={r.losingStudyId}>
                      Study {r.losingStudyId}: {r.errorMessage}
                    </li>
                  ))}
              </ul>
            ) : null}
          </div>
        ) : null}

        {errorMessage ? (
          <div className="rounded-md border border-[color:var(--color-novarad-red)]/40 bg-[color:var(--color-novarad-red)]/10 px-3 py-2 text-xs text-[color:var(--color-novarad-red)]">
            {errorMessage}
          </div>
        ) : null}

        <DialogActions>
          <Button
            variant="ghost"
            size="sm"
            onClick={onClose}
            disabled={submitting}
          >
            Cancel
          </Button>
          <Button
            variant="primary"
            size="sm"
            onClick={() => {
              if (keeperId != null && selectedCount > 0) {
                onSubmit(keeperId, Array.from(losingSelections), reason);
              }
            }}
            disabled={submitting || keeperId == null || selectedCount === 0}
            className="whitespace-nowrap"
          >
            {submitting ? <Spinner size={14} /> : <Merge className="size-4" />}
            Merge {selectedCount} into main study
          </Button>
        </DialogActions>
      </div>
    </Dialog>
  );
}

