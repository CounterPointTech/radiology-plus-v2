"use client";

import { useMutation } from "@tanstack/react-query";
import {
  AlertCircle,
  AlertTriangle,
  ChevronDown,
  ChevronRight,
  Info,
  Play,
} from "lucide-react";
import { useRouter } from "next/navigation";
import { useCallback, useEffect, useMemo, useState } from "react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Spinner } from "@/components/ui/spinner";
import { billingApi } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import {
  canAccessBilling,
  type ReconciliationDetailRow,
  type ReconciliationLineItem,
  type ReconciliationRun,
} from "@/lib/types";
import { formatDate, formatDateTime } from "@/lib/utils";

// Composite key identifying a single line (and therefore a single drill-down).
function lineKey(physicianId: number, cptCode: string, siteCode: string): string {
  return `${physicianId}|${cptCode}|${siteCode}`;
}

interface DetailState {
  loading: boolean;
  rows?: ReconciliationDetailRow[];
  error?: string;
}

function currentMonthBounds(): { from: string; to: string } {
  // Default window = the calendar month the user is currently in, UTC.
  // We render YYYY-MM-DD as the input value (HTML date inputs are local-time
  // calendar dates) and the request body converts back to UTC instants below.
  const now = new Date();
  const fromY = now.getUTCFullYear();
  const fromM = now.getUTCMonth();
  const from = new Date(Date.UTC(fromY, fromM, 1));
  const to = new Date(Date.UTC(fromY, fromM + 1, 1));
  return { from: toDateInputValue(from), to: toDateInputValue(to) };
}

function toDateInputValue(d: Date): string {
  return d.toISOString().slice(0, 10);
}

function dateInputToInstant(value: string): string {
  // Treat the picked date as a UTC midnight so the window matches the API's
  // inclusive-from / exclusive-to convention without timezone surprises.
  return `${value}T00:00:00Z`;
}

export default function ReconciliationPage() {
  const router = useRouter();
  const { user, isHydrated } = useAuth();

  const defaults = useMemo(currentMonthBounds, []);
  const [from, setFrom] = useState(defaults.from);
  const [to, setTo] = useState(defaults.to);
  const [site, setSite] = useState("");
  const [run, setRun] = useState<ReconciliationRun | null>(null);

  useEffect(() => {
    if (isHydrated && user && !canAccessBilling(user.role)) {
      router.replace("/validation");
    }
  }, [isHydrated, user, router]);

  const runMutation = useMutation({
    mutationFn: () =>
      billingApi.runReconciliation({
        from: dateInputToInstant(from),
        to: dateInputToInstant(to),
        site: site.trim() || null,
      }),
    onSuccess: (data) => setRun(data),
  });

  if (!isHydrated || !user) {
    return (
      <div className="min-h-[60vh] flex items-center justify-center">
        <Spinner size={28} />
      </div>
    );
  }

  const windowInvalid = !!from && !!to && to <= from;
  const canRun = !!from && !!to && !windowInvalid && !runMutation.isPending;

  return (
    <div className="mx-auto max-w-7xl px-6 py-8">
      <div className="mb-6">
        <p className="text-[10px] uppercase tracking-[0.3em] text-[color:var(--color-accent)]">
          Billing
        </p>
        <h1
          className="text-3xl mt-1"
          style={{ fontFamily: "var(--font-display)" }}
        >
          Reconciliation
        </h1>
        <p className="text-sm text-[color:var(--color-muted-fg)] mt-1">
          Run a per-radiologist, per-CPT work-RVU reconciliation for a window of
          signed reports. Bundles credited atomically against the CPT master.
        </p>
      </div>

      <section className="mb-6 rounded-lg border border-[color:var(--color-border)] bg-[color:var(--color-surface)] p-4">
        <div className="grid grid-cols-1 sm:grid-cols-4 gap-3 items-end">
          <label className="flex flex-col gap-1">
            <span className="text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)]">
              From
            </span>
            <input
              type="date"
              value={from}
              onChange={(e) => setFrom(e.target.value)}
              className="h-10 rounded-md border border-[color:var(--color-border)] bg-[color:var(--color-surface)] px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-accent)]/60"
            />
          </label>
          <label className="flex flex-col gap-1">
            <span className="text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)]">
              To (exclusive)
            </span>
            <input
              type="date"
              value={to}
              onChange={(e) => setTo(e.target.value)}
              className="h-10 rounded-md border border-[color:var(--color-border)] bg-[color:var(--color-surface)] px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-accent)]/60"
            />
          </label>
          <label className="flex flex-col gap-1">
            <span className="text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)]">
              Site (optional)
            </span>
            <input
              type="text"
              value={site}
              onChange={(e) => setSite(e.target.value)}
              placeholder="e.g. AHC, CRMCR"
              className="h-10 rounded-md border border-[color:var(--color-border)] bg-[color:var(--color-surface)] px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-accent)]/60"
            />
          </label>
          <Button
            size="md"
            onClick={() => runMutation.mutate()}
            disabled={!canRun}
            className="h-10"
          >
            {runMutation.isPending ? (
              <>
                <Spinner size={14} /> Running…
              </>
            ) : (
              <>
                <Play className="size-4" /> Run reconciliation
              </>
            )}
          </Button>
        </div>
        {windowInvalid ? (
          <p className="mt-3 text-xs text-[color:var(--color-novarad-red)]">
            <span className="inline-flex items-center gap-1">
              <AlertCircle className="size-3.5" />
              The end date must be after the start date.
            </span>
          </p>
        ) : null}
      </section>

      {runMutation.isError ? (
        <div className="mb-4 rounded-md border border-[color:var(--color-novarad-red)]/40 bg-[color:var(--color-novarad-red)]/10 px-3 py-3 text-sm text-[color:var(--color-novarad-red)] flex items-center gap-2">
          <AlertCircle className="size-4" />
          Couldn&apos;t run the reconciliation.{" "}
          <button
            className="underline underline-offset-2"
            onClick={() => runMutation.mutate()}
          >
            Try again
          </button>
        </div>
      ) : null}

      {run ? <ResultView run={run} /> : <EmptyHint />}
    </div>
  );
}

function EmptyHint() {
  return (
    <p className="text-sm text-[color:var(--color-muted-fg)]">
      Pick a window and run a reconciliation. Results appear here.
    </p>
  );
}

function ResultView({ run }: { run: ReconciliationRun }) {
  const byPhysician = useMemo(() => groupByPhysician(run.lineItems), [run.lineItems]);

  // "all" or a specific novaradPhysicianId; client-side filter over the run.
  const [physicianFilter, setPhysicianFilter] = useState<number | "all">("all");

  // Per-line expand state. Keyed by lineKey(physicianId, cptCode, siteCode).
  const [expandedKeys, setExpandedKeys] = useState<Set<string>>(() => new Set());
  // Per-line detail cache so re-expanding doesn't refetch.
  const [detailCache, setDetailCache] = useState<Map<string, DetailState>>(
    () => new Map(),
  );

  const visibleGroups = useMemo(
    () =>
      physicianFilter === "all"
        ? byPhysician
        : byPhysician.filter((g) => g.novaradPhysicianId === physicianFilter),
    [byPhysician, physicianFilter],
  );

  // All (physicianId, cpt, site) keys visible right now — used by Expand/Collapse all.
  const visibleKeys = useMemo(() => {
    const out: string[] = [];
    for (const g of visibleGroups) {
      for (const line of g.lines) {
        out.push(lineKey(g.novaradPhysicianId, line.cptCode, line.siteCode));
      }
    }
    return out;
  }, [visibleGroups]);

  const allExpanded =
    visibleKeys.length > 0 && visibleKeys.every((k) => expandedKeys.has(k));

  const fetchDetail = useCallback(
    async (physicianId: number, cptCode: string, siteCode: string) => {
      const key = lineKey(physicianId, cptCode, siteCode);
      setDetailCache((prev) => {
        const next = new Map(prev);
        next.set(key, { loading: true });
        return next;
      });
      try {
        const data = await billingApi.reconciliationLineDetail({
          runId: run.runId,
          physicianId,
          cptCode,
          siteCode,
        });
        setDetailCache((prev) => {
          const next = new Map(prev);
          next.set(key, { loading: false, rows: data.rows });
          return next;
        });
      } catch (err) {
        const message =
          err instanceof Error ? err.message : "Couldn't load detail.";
        setDetailCache((prev) => {
          const next = new Map(prev);
          next.set(key, { loading: false, error: message });
          return next;
        });
      }
    },
    [run.runId],
  );

  const toggleRow = useCallback(
    (physicianId: number, cptCode: string, siteCode: string) => {
      const key = lineKey(physicianId, cptCode, siteCode);
      setExpandedKeys((prev) => {
        const next = new Set(prev);
        if (next.has(key)) {
          next.delete(key);
        } else {
          next.add(key);
          // Fetch lazily on first expand. Cached results stay cached.
          if (!detailCache.has(key)) {
            void fetchDetail(physicianId, cptCode, siteCode);
          }
        }
        return next;
      });
    },
    [detailCache, fetchDetail],
  );

  const expandAll = useCallback(() => {
    setExpandedKeys(new Set(visibleKeys));
    // Fire fetches for anything not yet cached.
    for (const g of visibleGroups) {
      for (const line of g.lines) {
        const key = lineKey(g.novaradPhysicianId, line.cptCode, line.siteCode);
        if (!detailCache.has(key)) {
          void fetchDetail(g.novaradPhysicianId, line.cptCode, line.siteCode);
        }
      }
    }
  }, [detailCache, fetchDetail, visibleGroups, visibleKeys]);

  const collapseAll = useCallback(() => {
    setExpandedKeys(new Set());
  }, []);

  return (
    <>
      <RunSummary run={run} />
      {run.notes.length > 0 ? <NotesPanel notes={run.notes} /> : null}

      {run.lineItems.length === 0 ? (
        <div className="rounded-lg border border-[color:var(--color-border)] bg-[color:var(--color-surface)] px-6 py-10 text-center">
          <p className="text-sm text-[color:var(--color-muted-fg)]">
            No CPT credits in this window. Either there were no signed reports,
            or none of the signed reports had service-line CPTs against the
            joined billing service codes.
          </p>
        </div>
      ) : (
        <>
          <ResultControls
            physicians={byPhysician}
            physicianFilter={physicianFilter}
            setPhysicianFilter={setPhysicianFilter}
            allExpanded={allExpanded}
            anyVisible={visibleKeys.length > 0}
            onExpandAll={expandAll}
            onCollapseAll={collapseAll}
          />

          {visibleGroups.length === 0 ? (
            <div className="rounded-lg border border-[color:var(--color-border)] bg-[color:var(--color-surface)] px-6 py-10 text-center">
              <p className="text-sm text-[color:var(--color-muted-fg)]">
                No credits for the selected physician in this run.
              </p>
            </div>
          ) : (
            <div className="space-y-6">
              {visibleGroups.map((group) => (
                <PhysicianBlock
                  key={group.key}
                  group={group}
                  expandedKeys={expandedKeys}
                  detailCache={detailCache}
                  onToggleRow={toggleRow}
                />
              ))}
            </div>
          )}
        </>
      )}
    </>
  );
}

function ResultControls({
  physicians,
  physicianFilter,
  setPhysicianFilter,
  allExpanded,
  anyVisible,
  onExpandAll,
  onCollapseAll,
}: {
  physicians: PhysicianGroup[];
  physicianFilter: number | "all";
  setPhysicianFilter: (next: number | "all") => void;
  allExpanded: boolean;
  anyVisible: boolean;
  onExpandAll: () => void;
  onCollapseAll: () => void;
}) {
  return (
    <section className="mb-4 flex flex-wrap items-end justify-between gap-x-4 gap-y-3">
      <label className="flex flex-col gap-1">
        <span className="text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)]">
          Physician
        </span>
        <select
          value={physicianFilter === "all" ? "all" : String(physicianFilter)}
          onChange={(e) => {
            const v = e.target.value;
            setPhysicianFilter(v === "all" ? "all" : Number(v));
          }}
          className="h-10 min-w-[16rem] rounded-md border border-[color:var(--color-border)] bg-[color:var(--color-surface)] px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-accent)]/60"
        >
          <option value="all">All physicians ({physicians.length})</option>
          {physicians.map((g) => (
            <option key={g.novaradPhysicianId} value={g.novaradPhysicianId}>
              {g.physicianDisplayName}
            </option>
          ))}
        </select>
      </label>
      <Button
        variant="ghost"
        size="sm"
        onClick={allExpanded ? onCollapseAll : onExpandAll}
        disabled={!anyVisible}
        className="h-10"
      >
        {allExpanded ? (
          <>
            <ChevronDown className="size-4" /> Collapse all
          </>
        ) : (
          <>
            <ChevronRight className="size-4" /> Expand all
          </>
        )}
      </Button>
    </section>
  );
}

function RunSummary({ run }: { run: ReconciliationRun }) {
  return (
    <section className="mb-4 rounded-lg border border-[color:var(--color-border)] bg-[color:var(--color-surface)] p-4">
      <div className="flex flex-wrap items-end justify-between gap-x-6 gap-y-3">
        <div className="flex flex-wrap items-baseline gap-x-6 gap-y-1">
          <Stat label="Run" value={`#${run.runId}`} />
          <Stat label="Reports" value={run.totalReports.toString()} />
          <Stat label="Radiologists" value={run.totalRadiologists.toString()} />
          <Stat
            label="Total work RVU"
            value={run.totalWorkRvu.toFixed(2)}
            mono
          />
        </div>
        <span className="text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)]">
          Generated {formatDateTime(run.generatedAt)}
        </span>
      </div>
    </section>
  );
}

function Stat({
  label,
  value,
  mono = false,
}: {
  label: string;
  value: string;
  mono?: boolean;
}) {
  return (
    <div className="flex flex-col">
      <span className="text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)]">
        {label}
      </span>
      <span className={mono ? "font-mono text-lg tabular-nums" : "text-lg"}>
        {value}
      </span>
    </div>
  );
}

function NotesPanel({ notes }: { notes: ReconciliationRun["notes"] }) {
  return (
    <section className="mb-4 rounded-lg border border-[color:var(--color-caution)]/40 bg-[color:var(--color-caution)]/10 p-4">
      <div className="flex items-center gap-2 mb-2 text-[color:var(--color-caution)]">
        <AlertTriangle className="size-4" />
        <span className="text-sm font-medium">
          {notes.length} note{notes.length === 1 ? "" : "s"}
        </span>
      </div>
      <ul className="text-xs space-y-1">
        {notes.map((n, i) => (
          <li key={i} className="flex gap-2">
            <span className="font-mono text-[10px] uppercase tracking-wider text-[color:var(--color-muted-fg)] mt-0.5 shrink-0">
              {n.kind}
            </span>
            <span>{n.message}</span>
          </li>
        ))}
      </ul>
    </section>
  );
}

interface PhysicianGroup {
  key: string;
  novaradPhysicianId: number;
  physicianDisplayName: string;
  lines: ReconciliationLineItem[];
  totalReports: number;
  totalWorkRvu: number;
}

function groupByPhysician(lines: ReconciliationLineItem[]): PhysicianGroup[] {
  const map = new Map<number, PhysicianGroup>();
  for (const line of lines) {
    const existing = map.get(line.novaradPhysicianId);
    if (existing) {
      existing.lines.push(line);
      existing.totalWorkRvu += line.workRvuTotal;
    } else {
      map.set(line.novaradPhysicianId, {
        key: `${line.novaradPhysicianId}`,
        novaradPhysicianId: line.novaradPhysicianId,
        physicianDisplayName: line.physicianDisplayName,
        lines: [line],
        totalReports: 0,
        totalWorkRvu: line.workRvuTotal,
      });
    }
  }
  // Compute distinct-report count per physician across their lines.
  for (const g of map.values()) {
    const reportIds = new Set<number>();
    for (const line of g.lines) {
      for (const rid of line.novaradReportIds) reportIds.add(rid);
    }
    g.totalReports = reportIds.size;
    g.lines.sort((a, b) => a.cptCode.localeCompare(b.cptCode));
  }
  return [...map.values()].sort((a, b) =>
    a.physicianDisplayName.localeCompare(b.physicianDisplayName),
  );
}

function PhysicianBlock({
  group,
  expandedKeys,
  detailCache,
  onToggleRow,
}: {
  group: PhysicianGroup;
  expandedKeys: Set<string>;
  detailCache: Map<string, DetailState>;
  onToggleRow: (physicianId: number, cptCode: string, siteCode: string) => void;
}) {
  return (
    <div className="rounded-lg border border-[color:var(--color-border)] bg-[color:var(--color-surface)] overflow-hidden">
      <header className="px-4 py-3 bg-[color:var(--color-surface-2)] flex flex-wrap items-baseline justify-between gap-x-6 gap-y-1 border-b border-[color:var(--color-border)]">
        <h2 className="text-base font-medium">{group.physicianDisplayName}</h2>
        <div className="flex items-baseline gap-x-5 text-xs text-[color:var(--color-muted-fg)]">
          <span>
            <span className="font-mono tabular-nums">{group.totalReports}</span>{" "}
            report{group.totalReports === 1 ? "" : "s"}
          </span>
          <span>
            <span className="font-mono tabular-nums">
              {group.totalWorkRvu.toFixed(2)}
            </span>{" "}
            work RVU
          </span>
        </div>
      </header>
      <div className="overflow-x-auto">
        <table className="w-full text-sm">
          <thead className="text-left text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)] bg-[color:var(--color-surface-2)]/50">
            <tr>
              <th className="px-2 py-2.5 font-medium w-8" aria-label="Expand" />
              <th className="px-4 py-2.5 font-medium">CPT</th>
              <th className="px-4 py-2.5 font-medium">Description</th>
              <th className="px-4 py-2.5 font-medium w-20">Site</th>
              <th className="px-4 py-2.5 font-medium text-right w-20">Units</th>
              <th className="px-4 py-2.5 font-medium text-right w-24">RVU/unit</th>
              <th className="px-4 py-2.5 font-medium text-right w-24">Total RVU</th>
              <th className="px-4 py-2.5 font-medium w-32">Novarad RVU</th>
              <th className="px-4 py-2.5 font-medium text-right w-20">Reports</th>
            </tr>
          </thead>
          <tbody>
            {group.lines.map((line) => {
              const key = lineKey(
                group.novaradPhysicianId,
                line.cptCode,
                line.siteCode,
              );
              return (
                <LineRow
                  key={line.lineId}
                  line={line}
                  isExpanded={expandedKeys.has(key)}
                  detail={detailCache.get(key)}
                  onToggle={() =>
                    onToggleRow(
                      group.novaradPhysicianId,
                      line.cptCode,
                      line.siteCode,
                    )
                  }
                />
              );
            })}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function LineRow({
  line,
  isExpanded,
  detail,
  onToggle,
}: {
  line: ReconciliationLineItem;
  isExpanded: boolean;
  detail: DetailState | undefined;
  onToggle: () => void;
}) {
  const isBundle = line.cptCode.includes(";");
  const Chevron = isExpanded ? ChevronDown : ChevronRight;
  return (
    <>
      <tr
        className="border-t border-[color:var(--color-border)] hover:bg-[color:var(--color-surface-2)]/40 cursor-pointer"
        onClick={onToggle}
      >
        <td className="px-2 py-2 align-middle">
          <button
            type="button"
            aria-label={isExpanded ? "Collapse" : "Expand"}
            aria-expanded={isExpanded}
            onClick={(e) => {
              e.stopPropagation();
              onToggle();
            }}
            className="inline-flex items-center justify-center rounded p-1 hover:bg-[color:var(--color-surface-2)]"
          >
            <Chevron className="size-4 text-[color:var(--color-muted-fg)]" />
          </button>
        </td>
        <td className="px-4 py-2 font-mono text-xs">
          <span className="inline-flex items-center gap-2">
            <span>{line.cptCode}</span>
            {isBundle ? (
              <Badge variant="caution" title="Multi-CPT bundle credited once">
                bundle
              </Badge>
            ) : null}
          </span>
        </td>
        <td className="px-4 py-2 text-xs">
          {line.cptDescription ?? (
            <span className="italic text-[color:var(--color-muted-fg)]">—</span>
          )}
        </td>
        <td className="px-4 py-2 font-mono text-xs">{line.siteCode}</td>
        <td className="px-4 py-2 font-mono text-right tabular-nums">
          {line.units.toFixed(2)}
        </td>
        <td className="px-4 py-2 font-mono text-right tabular-nums">
          {line.workRvuPerUnit.toFixed(2)}
        </td>
        <td className="px-4 py-2 font-mono text-right tabular-nums font-medium">
          {line.workRvuTotal.toFixed(2)}
        </td>
        <td className="px-4 py-2 text-xs">
          {line.novaradRvuWork == null ? (
            <span className="text-[color:var(--color-muted-fg)]">—</span>
          ) : line.rvuMismatch ? (
            <span className="inline-flex items-center gap-1.5">
              <span className="font-mono tabular-nums">
                {line.novaradRvuWork.toFixed(2)}
              </span>
              <Badge
                variant="danger"
                title={`Novarad ${line.novaradRvuWork.toFixed(2)} ≠ ours ${line.workRvuPerUnit.toFixed(2)}`}
              >
                mismatch
              </Badge>
            </span>
          ) : (
            <span className="inline-flex items-center gap-1.5 text-[color:var(--color-muted-fg)]">
              <span className="font-mono tabular-nums">
                {line.novaradRvuWork.toFixed(2)}
              </span>
              <Info className="size-3" aria-label="Matches our master" />
            </span>
          )}
        </td>
        <td className="px-4 py-2 font-mono text-right tabular-nums text-xs text-[color:var(--color-muted-fg)]">
          {line.reportCount}
        </td>
      </tr>
      {isExpanded ? (
        <tr className="border-t border-[color:var(--color-border)] bg-[color:var(--color-surface-2)]/30">
          <td colSpan={9} className="px-4 py-3">
            <LineDetail detail={detail} />
          </td>
        </tr>
      ) : null}
    </>
  );
}

function LineDetail({ detail }: { detail: DetailState | undefined }) {
  if (!detail || detail.loading) {
    return (
      <div className="flex items-center gap-2 text-xs text-[color:var(--color-muted-fg)]">
        <Spinner size={12} /> Loading reports…
      </div>
    );
  }
  if (detail.error) {
    return (
      <div className="flex items-center gap-2 text-xs text-[color:var(--color-novarad-red)]">
        <AlertCircle className="size-3.5" /> {detail.error}
      </div>
    );
  }
  const rows = detail.rows ?? [];
  if (rows.length === 0) {
    return (
      <p className="text-xs text-[color:var(--color-muted-fg)]">
        No per-report detail available for this line.
      </p>
    );
  }
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-xs">
        <thead className="text-left text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)]">
          <tr>
            <th className="px-2 py-1.5 font-medium">Report</th>
            <th className="px-2 py-1.5 font-medium">Signed</th>
            <th className="px-2 py-1.5 font-medium">Accession</th>
            <th className="px-2 py-1.5 font-medium">Patient</th>
            <th className="px-2 py-1.5 font-medium">MRN</th>
            <th className="px-2 py-1.5 font-medium">DOB</th>
            <th className="px-2 py-1.5 font-medium">Sex</th>
            <th className="px-2 py-1.5 font-medium">Study date</th>
            <th className="px-2 py-1.5 font-medium">Modality</th>
            <th className="px-2 py-1.5 font-medium">Order</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr
              key={r.reportId}
              className="border-t border-[color:var(--color-border)]"
            >
              <td className="px-2 py-1.5 font-mono tabular-nums">{r.reportId}</td>
              <td className="px-2 py-1.5 whitespace-nowrap">
                {formatDateTime(r.signedAt)}
              </td>
              <td className="px-2 py-1.5 font-mono">
                {r.accession ?? (
                  <span className="italic text-[color:var(--color-muted-fg)]">
                    —
                  </span>
                )}
              </td>
              <td className="px-2 py-1.5">
                {formatPatient(r.patientLastName, r.patientFirstName)}
              </td>
              <td className="px-2 py-1.5 font-mono">
                {r.patientPid ?? (
                  <span className="italic text-[color:var(--color-muted-fg)]">
                    —
                  </span>
                )}
              </td>
              <td className="px-2 py-1.5 whitespace-nowrap">
                {r.patientBirthDate ? (
                  formatDate(r.patientBirthDate)
                ) : (
                  <span className="italic text-[color:var(--color-muted-fg)]">
                    —
                  </span>
                )}
              </td>
              <td className="px-2 py-1.5">
                {r.patientGender ?? (
                  <span className="italic text-[color:var(--color-muted-fg)]">
                    —
                  </span>
                )}
              </td>
              <td className="px-2 py-1.5 whitespace-nowrap">
                {r.studyDate ? (
                  formatDateTime(r.studyDate)
                ) : (
                  <span className="italic text-[color:var(--color-muted-fg)]">
                    —
                  </span>
                )}
              </td>
              <td className="px-2 py-1.5">
                {r.modality ?? (
                  <span className="italic text-[color:var(--color-muted-fg)]">
                    —
                  </span>
                )}
              </td>
              <td className="px-2 py-1.5 font-mono tabular-nums">{r.orderId}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function formatPatient(last: string | null, first: string | null) {
  const l = last?.trim();
  const f = first?.trim();
  const display = [l, f].filter(Boolean).join(", ");
  return display.length > 0 ? (
    display
  ) : (
    <span className="italic text-[color:var(--color-muted-fg)]">—</span>
  );
}
