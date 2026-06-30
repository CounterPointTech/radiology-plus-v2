"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { defaultRangeExtractor, useVirtualizer } from "@tanstack/react-virtual";
import {
  AlertCircle,
  Check,
  ChevronDown,
  ChevronRight,
  ExternalLink,
  Pencil,
  Plus,
  Search,
  X,
} from "lucide-react";
import { useRouter } from "next/navigation";
import { memo, useCallback, useEffect, useMemo, useRef, useState } from "react";

import { ImportUploader } from "@/components/billing/import-uploader";
import { MModalSyncPanel } from "@/components/billing/mmodal-sync-panel";
import { RvuImportUploader } from "@/components/billing/rvu-import-uploader";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Spinner } from "@/components/ui/spinner";
import { billingApi } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import {
  canAccessBilling,
  type CptMasterCmsRow,
  type RvuImport,
  type RvuOverride,
  type RvuQuarter,
} from "@/lib/types";
import { cn, formatDateTime } from "@/lib/utils";

const QUARTERS: RvuQuarter[] = ["A", "B", "C", "D"];
const QUARTER_ORDINAL: Record<RvuQuarter, string> = { A: "1st", B: "2nd", C: "3rd", D: "4th" };
const QUARTER_MONTHS: Record<RvuQuarter, string> = {
  A: "January to March",
  B: "April to June",
  C: "July to September",
  D: "October to December",
};
const CMS_PAGE = 200;
const MASTER_LIMIT = 2000;

// Shared grid template for the virtualized Master & overrides table — the header
// row and every code row use this so columns line up without <table> auto-layout.
// expand | code | description (flex) | master | CMS check | effective | override
const MASTER_GRID_COLS =
  "[grid-template-columns:2.5rem_13rem_minmax(16rem,1fr)_6rem_11rem_6rem_12rem]";

// Canonical key matching the backend's storage form, so site overrides (stored
// canonicalized) group back under the right master row. Mirrors NormalizeCpt
// (single) / NormalizeCptSetKey (bundle: ordinal-sorted, deduped, uppercased).
function canonCode(code: string): string {
  if (!code.includes(";")) return code.trim().toUpperCase();
  const parts = code.split(";").map((s) => s.trim().toUpperCase()).filter(Boolean);
  return Array.from(new Set(parts)).sort().join(";");
}

// One entry in the flattened virtual list: a master code row, one of its per-site
// override rows, or the "add a site override" row shown under an expanded code.
type RvuItem =
  | { kind: "code"; key: string; row: CptMasterCmsRow }
  | { kind: "site"; key: string; code: string; ov: RvuOverride }
  | { kind: "addSite"; key: string; code: string };

type Tab = "cms" | "master" | "mmodal";

function useDebounced<T>(value: T, ms = 250): T {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const t = setTimeout(() => setDebounced(value), ms);
    return () => clearTimeout(t);
  }, [value, ms]);
  return debounced;
}

const fmt = (n: number | null | undefined) =>
  n == null ? "—" : n.toFixed(2);

export default function RvuPage() {
  const router = useRouter();
  const { user, isHydrated } = useAuth();

  const currentYear = new Date().getFullYear();
  const [year, setYear] = useState(currentYear);
  const [tab, setTab] = useState<Tab>("master");

  // Role gate — Billing is NRS+Admin only, enforced here in addition to the server.
  useEffect(() => {
    if (isHydrated && user && !canAccessBilling(user.role)) {
      router.replace("/validation");
    }
  }, [isHydrated, user, router]);

  const imports = useQuery({
    queryKey: ["rvu-imports"],
    queryFn: () => billingApi.listRvuImports(10),
    enabled: !!user && canAccessBilling(user.role),
  });

  const yearOptions = useMemo(() => {
    const ys = new Set<number>([currentYear]);
    if (imports.data) for (const i of imports.data) ys.add(i.year);
    return [...ys].sort((a, b) => b - a);
  }, [imports.data, currentYear]);

  if (!isHydrated || !user) {
    return (
      <div className="min-h-[60vh] flex items-center justify-center">
        <Spinner size={28} />
      </div>
    );
  }

  return (
    <div className="mx-auto max-w-7xl px-6 py-8">
      <div className="flex flex-wrap items-end justify-between gap-4 mb-6">
        <div>
          <p className="text-[10px] uppercase tracking-[0.3em] text-[color:var(--color-accent)]">
            Billing
          </p>
          <h1 className="text-3xl mt-1" style={{ fontFamily: "var(--font-display)" }}>
            CPT &amp; RVU
          </h1>
          <p className="text-sm text-[color:var(--color-muted-fg)] mt-1">
            The CPT master and its work RVUs, the CMS PFS source of truth it
            reconciles against, and per-site overrides.
          </p>
        </div>
        <div className="flex items-center gap-2">
          <label className="text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)]">
            Year
          </label>
          <select
            value={year}
            onChange={(e) => setYear(Number(e.target.value))}
            className="h-10 rounded-md border border-[color:var(--color-border)] bg-[color:var(--color-surface)] px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-accent)]/60"
          >
            {yearOptions.map((y) => (
              <option key={y} value={y}>
                {y}
              </option>
            ))}
          </select>
        </div>
      </div>

      <div className="inline-flex rounded-lg border border-[color:var(--color-border)] bg-[color:var(--color-surface)] p-1 mb-6">
        <TabButton active={tab === "master"} onClick={() => setTab("master")}>
          Master &amp; overrides
        </TabButton>
        <TabButton active={tab === "cms"} onClick={() => setTab("cms")}>
          CMS values
        </TabButton>
        <TabButton active={tab === "mmodal"} onClick={() => setTab("mmodal")}>
          Sync to M*Modal
        </TabButton>
      </div>

      {tab === "master" ? (
        <MasterOverridesTab year={year} />
      ) : tab === "cms" ? (
        <CmsValuesTab year={year} imports={imports.data ?? []} />
      ) : (
        <MModalSyncPanel year={year} />
      )}
    </div>
  );
}

function TabButton({
  active,
  onClick,
  children,
}: {
  active: boolean;
  onClick: () => void;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={cn(
        "px-4 py-1.5 rounded-md text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-accent)]/50",
        active
          ? "bg-[color:var(--color-accent)]/15 text-[color:var(--color-accent)]"
          : "text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-base-fg)]",
      )}
      aria-pressed={active}
    >
      {children}
    </button>
  );
}

// ===========================================================================
// Tab 1 — CMS values browser + PPRRVU importer
// ===========================================================================

function CmsValuesTab({ year, imports }: { year: number; imports: RvuImport[] }) {
  const queryClient = useQueryClient();
  const [quarter, setQuarter] = useState<RvuQuarter>("A");
  const [search, setSearch] = useState("");
  const debouncedSearch = useDebounced(search);

  const values = useQuery({
    queryKey: ["rvu-values", year, quarter, debouncedSearch],
    queryFn: () =>
      billingApi.listRvuValues({
        year,
        quarter,
        q: debouncedSearch || undefined,
        limit: CMS_PAGE,
      }),
  });

  const rows = values.data ?? [];
  const lastImport = useMemo(
    () =>
      imports.find((i) => i.year === year && i.quarter === quarter) ??
      imports.find((i) => i.year === year) ??
      null,
    [imports, year, quarter],
  );

  return (
    <>
      <div className="mb-4 flex flex-wrap items-center gap-2">
        <label className="text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)]">
          Quarter
        </label>
        <div className="inline-flex rounded-md border border-[color:var(--color-border)] overflow-hidden">
          {QUARTERS.map((q) => (
            <button
              key={q}
              type="button"
              onClick={() => setQuarter(q)}
              title={`${QUARTER_MONTHS[q]} · CMS file rvu${String(year).slice(2)}${q.toLowerCase()}.zip`}
              className={cn(
                "px-3 py-1.5 text-sm border-r border-[color:var(--color-border)] last:border-r-0",
                quarter === q
                  ? "bg-[color:var(--color-accent)]/15 text-[color:var(--color-accent)]"
                  : "text-[color:var(--color-muted-fg)] hover:bg-[color:var(--color-surface-2)]/60",
              )}
              aria-pressed={quarter === q}
            >
              {QUARTER_ORDINAL[q]}
            </button>
          ))}
        </div>
        <a
          href="https://www.cms.gov/medicare/payment/fee-schedules/physician/pfs-relative-value-files"
          target="_blank"
          rel="noopener noreferrer"
          className="ml-auto inline-flex items-center gap-1 text-xs text-[color:var(--color-accent)] hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-accent)]/50 rounded"
        >
          Get the latest PPRRVU files from CMS
          <ExternalLink className="size-3.5" />
        </a>
      </div>

      <RvuImportUploader
        year={year}
        quarter={quarter}
        onImported={() => {
          queryClient.invalidateQueries({ queryKey: ["rvu-values"] });
          queryClient.invalidateQueries({ queryKey: ["rvu-imports"] });
          queryClient.invalidateQueries({ queryKey: ["cms-check"] });
        }}
      />

      {lastImport ? (
        <div className="mb-4 rounded-md border border-[color:var(--color-border)] bg-[color:var(--color-surface)] px-4 py-3 text-xs flex flex-wrap items-center gap-x-4 gap-y-1">
          <span className="text-[color:var(--color-muted-fg)]">Last import:</span>
          <span className="font-mono">{lastImport.fileName}</span>
          <span className="text-[color:var(--color-muted-fg)]">·</span>
          <span>
            {lastImport.year} · {QUARTER_ORDINAL[lastImport.quarter]} quarter
          </span>
          <span className="text-[color:var(--color-muted-fg)]">·</span>
          <span>
            <strong>{lastImport.parsedRows}</strong> parsed (
            <span className="text-[color:var(--color-accent)]">
              {lastImport.insertedRows} inserted
            </span>
            , <span className="text-[color:var(--color-muted-fg)]">{lastImport.updatedRows} updated</span>
            {lastImport.skippedRows > 0 ? (
              <>
                , <span className="text-[color:var(--color-caution)]">{lastImport.skippedRows} skipped</span>
              </>
            ) : null}
            )
          </span>
          <span className="text-[color:var(--color-muted-fg)]">·</span>
          <span className="text-[color:var(--color-muted-fg)]">{formatDateTime(lastImport.ranAt)}</span>
        </div>
      ) : null}

      <div className="mb-3 flex items-center gap-2">
        <div className="relative flex-1 max-w-md">
          <Search className="absolute left-3 top-1/2 -translate-y-1/2 size-4 text-[color:var(--color-muted-fg)]" />
          <Input
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder="Search by HCPCS or description"
            aria-label="Search CMS RVU values"
            className="pl-9"
          />
        </div>
        {values.isFetching ? <Spinner size={16} /> : null}
        <span className="ml-auto text-xs text-[color:var(--color-muted-fg)]">
          {values.data ? `${rows.length} shown${rows.length === CMS_PAGE ? "+" : ""}` : null}
        </span>
      </div>

      {values.isError ? (
        <ErrorBar label="Couldn't load the CMS RVU values." onRetry={() => values.refetch()} />
      ) : null}

      <div className="rounded-lg border border-[color:var(--color-border)] bg-[color:var(--color-surface)] overflow-hidden">
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead className="text-left text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)] bg-[color:var(--color-surface-2)]">
              <tr>
                <th className="px-3 py-3 font-medium cursor-help" title="HCPCS/CPT procedure code (the CMS billing code).">HCPCS</th>
                <th className="px-3 py-3 font-medium cursor-help" title="Modifier: blank = global, 26 = professional component, TC = technical component.">Mod</th>
                <th className="px-3 py-3 font-medium cursor-help" title="CMS short descriptor for the code (AMA copyright).">Description</th>
                <th className="px-3 py-3 font-medium text-right cursor-help" title="Work RVU: the physician-effort component. This is the value reconciliation credits against.">Work</th>
                <th className="px-3 py-3 font-medium text-right cursor-help" title="Practice Expense RVU, non-facility (office / outpatient) setting.">PE nf</th>
                <th className="px-3 py-3 font-medium text-right cursor-help" title="Practice Expense RVU, facility (hospital) setting.">PE fac</th>
                <th className="px-3 py-3 font-medium text-right cursor-help" title="Malpractice RVU component.">MP</th>
                <th className="px-3 py-3 font-medium text-right cursor-help" title="Total RVU, non-facility (work + non-facility PE + malpractice).">Total nf</th>
                <th className="px-3 py-3 font-medium text-right cursor-help" title="Total RVU, facility (work + facility PE + malpractice).">Total fac</th>
                <th className="px-3 py-3 font-medium cursor-help" title="CMS status indicator. A = active / separately payable; other values are not separately payable.">Status</th>
                <th className="px-3 py-3 font-medium cursor-help" title="Global surgery period in days (000 / 010 / 090), or XXX / YYY / ZZZ for non-surgical codes.">Global</th>
              </tr>
            </thead>
            <tbody>
              {values.isLoading ? (
                <tr>
                  <td colSpan={11} className="px-4 py-10 text-center text-[color:var(--color-muted-fg)]">
                    <Spinner size={20} />
                  </td>
                </tr>
              ) : rows.length === 0 && !values.isError ? (
                <tr>
                  <td colSpan={11} className="px-4 py-10 text-center text-sm text-[color:var(--color-muted-fg)]">
                    {debouncedSearch
                      ? `No CMS values matching "${debouncedSearch}" in ${year} Q${quarter}.`
                      : `No CMS values for ${year} Q${quarter}. Import a PPRRVU file to load this snapshot.`}
                  </td>
                </tr>
              ) : (
                rows.map((r) => (
                  <tr
                    key={`${r.hcpcs}-${r.modifier}`}
                    className="border-t border-[color:var(--color-border)] hover:bg-[color:var(--color-surface-2)]/50"
                  >
                    <td className="px-3 py-2.5 font-mono text-xs">{r.hcpcs}</td>
                    <td className="px-3 py-2.5 font-mono text-xs text-[color:var(--color-muted-fg)]">
                      {r.modifier || "—"}
                    </td>
                    <td className="px-3 py-2.5 max-w-md truncate" title={r.description ?? undefined}>
                      {r.description ?? <span className="text-[color:var(--color-muted-fg)] italic">—</span>}
                    </td>
                    <td className="px-3 py-2.5 font-mono text-right tabular-nums">{fmt(r.workRvu)}</td>
                    <td className="px-3 py-2.5 font-mono text-right tabular-nums text-[color:var(--color-muted-fg)]">{fmt(r.peRvuNonFac)}</td>
                    <td className="px-3 py-2.5 font-mono text-right tabular-nums text-[color:var(--color-muted-fg)]">{fmt(r.peRvuFac)}</td>
                    <td className="px-3 py-2.5 font-mono text-right tabular-nums text-[color:var(--color-muted-fg)]">{fmt(r.mpRvu)}</td>
                    <td className="px-3 py-2.5 font-mono text-right tabular-nums text-[color:var(--color-muted-fg)]">{fmt(r.totalNonFac)}</td>
                    <td className="px-3 py-2.5 font-mono text-right tabular-nums text-[color:var(--color-muted-fg)]">{fmt(r.totalFac)}</td>
                    <td className="px-3 py-2.5">
                      <StatusBadge status={r.statusCode} />
                    </td>
                    <td className="px-3 py-2.5 font-mono text-xs text-[color:var(--color-muted-fg)]">
                      {r.globalDays || "—"}
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      </div>

      {rows.length === CMS_PAGE ? (
        <p className="mt-3 text-xs text-[color:var(--color-muted-fg)]">
          Showing the first {CMS_PAGE} rows. Refine your search to narrow the list.
        </p>
      ) : null}
    </>
  );
}

function StatusBadge({ status }: { status: string | null }) {
  if (!status) return <span className="text-[color:var(--color-muted-fg)]">—</span>;
  const active = status.toUpperCase() === "A";
  return (
    <Badge
      variant={active ? "success" : "neutral"}
      title={
        active
          ? "Active: separately payable, so this code earns work-RVU credit"
          : `Status ${status}: not separately payable`
      }
    >
      {status}
    </Badge>
  );
}

// ===========================================================================
// Tab 2 — CPT master ⇄ CMS reconciliation + manual overrides
// ===========================================================================

function MasterOverridesTab({ year }: { year: number }) {
  const queryClient = useQueryClient();
  const [search, setSearch] = useState("");
  const [onlyNonReconciling, setOnlyNonReconciling] = useState(false);
  const debouncedSearch = useDebounced(search);
  // Which code rows are expanded to reveal their per-site overrides.
  const [expanded, setExpanded] = useState<Set<string>>(() => new Set());
  // The virtual item key of the row whose cell is being inline-edited. The virtualizer
  // force-keeps that row mounted so an uncommitted draft survives scrolling it off-screen.
  const [editingKey, setEditingKey] = useState<string | null>(null);

  const check = useQuery({
    queryKey: ["cms-check", year],
    queryFn: () => billingApi.cptMasterCmsCheck({ year, limit: MASTER_LIMIT }),
  });

  // All overrides for the year; the site-specific ones group under their code for the
  // expandable per-site rows (the tenant-wide ones still show in the Override column).
  const siteOv = useQuery({
    queryKey: ["rvu-site-overrides", year],
    queryFn: () => billingApi.listRvuOverrides({ year }),
  });

  const upsert = useMutation({
    mutationFn: (v: { code: string; overrideWorkRvu: number }) =>
      billingApi.upsertRvuOverride(v.code, { year, overrideWorkRvu: v.overrideWorkRvu }),
    // Optimistic: an override always wins, so the effective value becomes the override
    // immediately — no waiting on the refetch. onSettled still reconciles with the server.
    onMutate: async (v) => {
      await queryClient.cancelQueries({ queryKey: ["cms-check", year] });
      // Snapshot only THIS row's prior values; a whole-array restore on error could
      // revert a concurrent row's still-valid optimistic patch.
      const prevRow = queryClient
        .getQueryData<CptMasterCmsRow[]>(["cms-check", year])
        ?.find((r) => r.code === v.code);
      queryClient.setQueryData<CptMasterCmsRow[]>(["cms-check", year], (rows) =>
        rows?.map((r) =>
          r.code === v.code
            ? { ...r, overrideWorkRvu: v.overrideWorkRvu, effectiveWorkRvu: v.overrideWorkRvu }
            : r,
        ),
      );
      return { prevRow };
    },
    onError: (_e, v, ctx) => {
      if (!ctx?.prevRow) return;
      queryClient.setQueryData<CptMasterCmsRow[]>(["cms-check", year], (rows) =>
        rows?.map((r) => (r.code === v.code ? ctx.prevRow! : r)),
      );
    },
    onSettled: () => queryClient.invalidateQueries({ queryKey: ["cms-check", year] }),
  });

  const clear = useMutation({
    mutationFn: (code: string) => billingApi.deleteRvuOverride(code, year),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["cms-check", year] }),
  });

  // Edit the underlying CPT master row (description / work RVU) — the capability
  // formerly on the standalone CPT master page, now merged here.
  const patch = useMutation({
    mutationFn: (v: { code: string; workRvu?: number; description?: string }) =>
      billingApi.patchCptCode(v.code, { year, workRvu: v.workRvu, description: v.description }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["cms-check", year] }),
  });

  // Site-specific override mutations, kept separate from the tenant-wide upsert/clear
  // so their pending state and invalidation stay independent. Site overrides don't
  // change the tenant-wide cms-check rows, so they invalidate only the site list.
  const siteUpsert = useMutation({
    mutationFn: (v: { code: string; siteCode: string; overrideWorkRvu: number }) =>
      billingApi.upsertRvuOverride(v.code, {
        year,
        overrideWorkRvu: v.overrideWorkRvu,
        siteCode: v.siteCode,
      }),
    onSuccess: () =>
      queryClient.invalidateQueries({ queryKey: ["rvu-site-overrides", year] }),
  });
  const siteClear = useMutation({
    mutationFn: (v: { code: string; siteCode: string }) =>
      billingApi.deleteRvuOverride(v.code, year, v.siteCode),
    onSuccess: () =>
      queryClient.invalidateQueries({ queryKey: ["rvu-site-overrides", year] }),
  });

  const all = check.data ?? [];
  const nonReconcilingCount = useMemo(
    () => all.filter((r) => isNonReconciling(r.verdict)).length,
    [all],
  );

  const rows = useMemo(() => {
    const q = debouncedSearch.trim().toLowerCase();
    return all.filter((r) => {
      if (onlyNonReconciling && !isNonReconciling(r.verdict)) return false;
      if (!q) return true;
      return (
        r.code.toLowerCase().includes(q) ||
        r.description.toLowerCase().includes(q)
      );
    });
  }, [all, debouncedSearch, onlyNonReconciling]);

  // Site overrides grouped under their (canonical) code, so each master row knows
  // its per-site overrides without an O(rows × overrides) scan per render.
  const siteOverridesByCode = useMemo(() => {
    const m = new Map<string, RvuOverride[]>();
    for (const o of siteOv.data ?? []) {
      if (!o.siteCode) continue; // tenant-wide ones show in the Override column
      const k = canonCode(o.code);
      const arr = m.get(k);
      if (arr) arr.push(o);
      else m.set(k, [o]);
    }
    for (const arr of m.values())
      arr.sort((a, b) => (a.siteCode ?? "").localeCompare(b.siteCode ?? ""));
    return m;
  }, [siteOv.data]);

  // Flatten code rows + (when expanded) their site-override rows + an add-row into one
  // virtualized list, mirroring the worklist's flattenGroups so expansion stays cheap.
  const items = useMemo<RvuItem[]>(() => {
    const out: RvuItem[] = [];
    for (const r of rows) {
      out.push({ kind: "code", key: r.code, row: r });
      if (expanded.has(r.code)) {
        const sos = siteOverridesByCode.get(canonCode(r.code)) ?? [];
        for (const ov of sos)
          out.push({ kind: "site", key: `${r.code}::${ov.siteCode}`, code: r.code, ov });
        out.push({ kind: "addSite", key: `${r.code}::__add`, code: r.code });
      }
    }
    return out;
  }, [rows, expanded, siteOverridesByCode]);

  const pendingCode =
    (upsert.isPending && upsert.variables?.code) ||
    (clear.isPending && clear.variables) ||
    null;

  // Stable handlers so memoized rows don't re-render on unrelated state. React
  // Query's mutate fns are referentially stable, so these useCallbacks are too.
  const { mutate: upsertMutate } = upsert;
  const { mutate: clearMutate } = clear;
  const { mutate: patchMutate } = patch;
  const handleSave = useCallback(
    (code: string, value: number) => upsertMutate({ code, overrideWorkRvu: value }),
    [upsertMutate],
  );
  const handleClear = useCallback(
    (code: string) => clearMutate(code),
    [clearMutate],
  );
  const handlePatch = useCallback(
    (code: string, p: { workRvu?: number; description?: string }) =>
      patchMutate({ code, ...p }),
    [patchMutate],
  );

  const { mutate: siteUpsertMutate } = siteUpsert;
  const { mutate: siteClearMutate } = siteClear;
  const handleToggleExpand = useCallback((code: string) => {
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(code)) next.delete(code);
      else next.add(code);
      return next;
    });
  }, []);
  const handleSaveSite = useCallback(
    (code: string, siteCode: string, value: number) =>
      siteUpsertMutate({ code, siteCode, overrideWorkRvu: value }),
    [siteUpsertMutate],
  );
  const handleClearSite = useCallback(
    (code: string, siteCode: string) => siteClearMutate({ code, siteCode }),
    [siteClearMutate],
  );
  // A cell entered/left edit mode; remember its row key (only one cell edits at a time).
  const handleRowEditing = useCallback((key: string, active: boolean) => {
    setEditingKey((cur) => (active ? key : cur === key ? null : cur));
  }, []);

  // The single site row (code::site) whose save/clear is in flight, for its spinner.
  const savingSiteKey =
    siteUpsert.isPending && siteUpsert.variables
      ? `${siteUpsert.variables.code}::${siteUpsert.variables.siteCode}`
      : siteClear.isPending && siteClear.variables
        ? `${siteClear.variables.code}::${siteClear.variables.siteCode}`
        : null;

  // Virtualize the master table — all ~564 rich rows (plus any expanded site rows)
  // would otherwise re-render on every override mutation (the freeze). Only the
  // ~visible items render now; the ~238ms server-authoritative refetch repaints those.
  const scrollParentRef = useRef<HTMLDivElement>(null);
  const editingIndex = editingKey == null ? -1 : items.findIndex((it) => it.key === editingKey);
  const rowVirtualizer = useVirtualizer({
    count: items.length,
    getScrollElement: () => scrollParentRef.current,
    estimateSize: () => 52,
    overscan: 10,
    getItemKey: (index) => items[index]!.key,
    // Keep the row being edited in the rendered set even when scrolled away, so its
    // uncommitted draft isn't unmounted. No-op (default behavior) when nothing's edited.
    rangeExtractor: useCallback(
      (range: { startIndex: number; endIndex: number; overscan: number; count: number }) => {
        const def = defaultRangeExtractor(range);
        if (editingIndex < 0 || def.includes(editingIndex)) return def;
        return [...def, editingIndex].sort((a, b) => a - b);
      },
      [editingIndex],
    ),
  });

  return (
    <>
      <ImportUploader
        year={year}
        onImported={() => {
          queryClient.invalidateQueries({ queryKey: ["cms-check", year] });
          queryClient.invalidateQueries({ queryKey: ["cpt-imports"] });
        }}
      />

      <div className="mb-3 flex flex-wrap items-center gap-3">
        <div className="relative flex-1 max-w-md">
          <Search className="absolute left-3 top-1/2 -translate-y-1/2 size-4 text-[color:var(--color-muted-fg)]" />
          <Input
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder="Search by CPT/bundle or description"
            aria-label="Search CPT master"
            className="pl-9"
          />
        </div>
        {check.isFetching ? <Spinner size={16} /> : null}
        <label className="inline-flex items-center gap-2 text-sm cursor-pointer select-none">
          <input
            type="checkbox"
            checked={onlyNonReconciling}
            onChange={(e) => setOnlyNonReconciling(e.target.checked)}
            className="size-4 accent-[color:var(--color-accent)]"
          />
          <span>
            Only non-reconciling
            {nonReconcilingCount > 0 ? (
              <Badge variant="accent" className="ml-2">
                {nonReconcilingCount}
              </Badge>
            ) : null}
          </span>
        </label>
        <span className="ml-auto text-xs text-[color:var(--color-muted-fg)]">
          {check.data ? `${rows.length} of ${all.length}` : null}
        </span>
      </div>

      <p className="mb-3 text-xs text-[color:var(--color-muted-fg)]">
        <strong>Effective</strong> is what reconciliation credits: the override if set,
        otherwise CMS, otherwise the master sheet&apos;s value. For a bundle, CMS check
        compares the master&apos;s stored RVU to the sum of its CMS component work-RVUs.
        Expand a code to set per-site overrides, which win over the tenant-wide value at
        that site.
      </p>

      {check.isError ? (
        <ErrorBar label="Couldn't load the CPT master / CMS check." onRetry={() => check.refetch()} />
      ) : null}

      {upsert.isError ||
      clear.isError ||
      siteUpsert.isError ||
      siteClear.isError ||
      patch.isError ? (
        <p className="mb-3 text-sm text-[color:var(--color-novarad-red)]">
          Couldn&apos;t save that change. Try again, or refresh if it keeps failing.
        </p>
      ) : null}

      <div
        role="table"
        aria-rowcount={items.length}
        className="rounded-lg border border-[color:var(--color-border)] bg-[color:var(--color-surface)] overflow-hidden"
      >
        {/* Header row — outside the scroll container so it stays put while the
            rows scroll. Mirrors the worklist's virtualized layout. */}
        <div
          role="row"
          className={cn(
            "grid items-center text-left text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)] bg-[color:var(--color-surface-2)] border-b border-[color:var(--color-border)]",
            MASTER_GRID_COLS,
          )}
        >
          <span role="columnheader" aria-label="Expand per-site overrides" className="px-2 py-3" />
          <span role="columnheader" className="px-4 py-3 font-medium cursor-help" title="The CPT code, or a semicolon-separated multi-code bundle, from the master sheet.">Code</span>
          <span role="columnheader" className="px-4 py-3 font-medium cursor-help" title="Procedure description from the CPT master.">Description</span>
          <span role="columnheader" className="px-4 py-3 font-medium text-right cursor-help" title="Work RVU from the imported CPT master sheet (this site's curated source).">Master</span>
          <span role="columnheader" className="px-4 py-3 font-medium cursor-help" title="How the master RVU compares to CMS: a single code against the CMS work RVU, or a bundle against the sum of its CMS component work RVUs.">CMS check</span>
          <span role="columnheader" className="px-4 py-3 font-medium text-right cursor-help" title="The work RVU reconciliation actually credits: the override if set, otherwise CMS, otherwise the master value.">Effective</span>
          <span role="columnheader" className="px-4 py-3 font-medium cursor-help" title="A manual tenant-wide work-RVU override that wins over CMS and the master. Set, edit, or clear it here.">Override</span>
        </div>

        {check.isLoading ? (
          <div className="px-4 py-10 text-center text-[color:var(--color-muted-fg)]">
            <Spinner size={20} />
          </div>
        ) : rows.length === 0 && !check.isError ? (
          <div className="px-4 py-10 text-center text-sm text-[color:var(--color-muted-fg)]">
            {onlyNonReconciling
              ? "Every code reconciles to CMS for this filter. 🎉"
              : debouncedSearch
                ? `No codes matching "${debouncedSearch}" in ${year}.`
                : `No CPT master rows for ${year}.`}
          </div>
        ) : (
          // Bounded scroll container so the virtualizer has a fixed-height
          // viewport to measure against; the contain hint scopes layout
          // invalidation to this subtree.
          <div
            ref={scrollParentRef}
            role="rowgroup"
            className="overflow-y-auto [contain:layout_paint] [max-height:calc(100vh-360px)] [min-height:24rem]"
          >
            <div
              style={{
                height: `${rowVirtualizer.getTotalSize()}px`,
                position: "relative",
              }}
            >
              {rowVirtualizer.getVirtualItems().map((vi) => {
                const item = items[vi.index]!;
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
                    {item.kind === "code" ? (
                      <MasterRow
                        row={item.row}
                        siteCount={(siteOverridesByCode.get(canonCode(item.row.code)) ?? []).length}
                        expanded={expanded.has(item.row.code)}
                        onToggleExpand={handleToggleExpand}
                        saving={pendingCode === item.row.code}
                        patching={patch.isPending && patch.variables?.code === item.row.code}
                        onSave={handleSave}
                        onClear={handleClear}
                        onPatch={handlePatch}
                        onEditing={handleRowEditing}
                      />
                    ) : item.kind === "site" ? (
                      <SiteOverrideRow
                        code={item.code}
                        ov={item.ov}
                        saving={savingSiteKey === `${item.code}::${item.ov.siteCode}`}
                        onSave={handleSaveSite}
                        onClear={handleClearSite}
                        onEditing={handleRowEditing}
                      />
                    ) : (
                      <AddSiteRow
                        code={item.code}
                        existingSites={
                          (siteOverridesByCode.get(canonCode(item.code)) ?? [])
                            .map((o) => o.siteCode)
                            .filter((s): s is string => s != null)
                        }
                        pendingSiteCode={
                          siteUpsert.isPending && siteUpsert.variables?.code === item.code
                            ? siteUpsert.variables?.siteCode ?? null
                            : null
                        }
                        onSave={handleSaveSite}
                      />
                    )}
                  </div>
                );
              })}
            </div>
          </div>
        )}
      </div>
    </>
  );
}

function isNonReconciling(verdict: CptMasterCmsRow["verdict"]): boolean {
  return (
    verdict === "differs" ||
    verdict === "differs_sum" ||
    verdict === "partial" ||
    verdict === "not_in_cms"
  );
}

const MasterRow = memo(function MasterRow({
  row,
  siteCount,
  expanded,
  onToggleExpand,
  saving,
  patching,
  onSave,
  onClear,
  onPatch,
  onEditing,
}: {
  row: CptMasterCmsRow;
  siteCount: number;
  expanded: boolean;
  onToggleExpand: (code: string) => void;
  saving: boolean;
  patching: boolean;
  onSave: (code: string, value: number) => void;
  onClear: (code: string) => void;
  onPatch: (code: string, patch: { workRvu?: number; description?: string }) => void;
  onEditing: (key: string, active: boolean) => void;
}) {
  // Any inline edit in this row maps to the row's virtual key, so the virtualizer can
  // keep the row mounted (draft-preserving) while it's being edited. Stable across
  // re-renders so an IDLE cell's report-effect doesn't re-fire and clobber editingKey
  // (last-writer-wins) when the row re-renders while a different cell is mid-edit.
  const onEditingChange = useCallback(
    (active: boolean) => onEditing(row.code, active),
    [onEditing, row.code],
  );
  return (
    <div
      role="row"
      className={cn(
        "grid items-start border-t border-[color:var(--color-border)] hover:bg-[color:var(--color-surface-2)]/50 text-sm",
        MASTER_GRID_COLS,
      )}
    >
      <div role="cell" className="flex items-start justify-center pt-2.5">
        <button
          type="button"
          onClick={() => onToggleExpand(row.code)}
          aria-expanded={expanded}
          aria-label={`${expanded ? "Hide" : "Show"} per-site overrides for ${row.code}`}
          title="Per-site overrides"
          className={cn(
            "rounded p-0.5 hover:bg-[color:var(--color-surface-2)] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-accent)]/50",
            siteCount > 0
              ? "text-[color:var(--color-accent)]"
              : "text-[color:var(--color-muted-fg)]",
          )}
        >
          {expanded ? <ChevronDown className="size-4" /> : <ChevronRight className="size-4" />}
        </button>
      </div>
      <div role="cell" className="px-4 py-2.5 font-mono text-xs">
        {row.isBundle ? (
          <span className="inline-flex items-center gap-2">
            <span className="break-all">{row.code}</span>
            <Badge variant="accent" title="Multi-code bundle (master sheet sum)">
              bundle
            </Badge>
          </span>
        ) : (
          row.code
        )}
      </div>
      <div role="cell" className="px-4 py-2.5 min-w-0">
        <EditableCell
          value={row.description}
          type="text"
          ariaLabel={`Description for ${row.code}`}
          saving={patching}
          onSave={(v) =>
            v !== row.description ? onPatch(row.code, { description: v }) : undefined
          }
          onEditingChange={onEditingChange}
        />
      </div>
      <div role="cell" className="px-4 py-2.5 font-mono text-right tabular-nums">
        <EditableCell
          value={row.masterWorkRvu == null ? "" : row.masterWorkRvu.toFixed(2)}
          type="number"
          align="right"
          ariaLabel={`Master work RVU for ${row.code}`}
          saving={patching}
          onSave={(v) => {
            const n = Number(v);
            if (!Number.isFinite(n) || n < 0) return;
            if (n !== row.masterWorkRvu) onPatch(row.code, { workRvu: n });
          }}
          onEditingChange={onEditingChange}
        />
      </div>
      <div role="cell" className="px-4 py-2.5">
        <CmsCheckBadge row={row} />
      </div>
      <div role="cell" className="px-4 py-2.5 font-mono text-right tabular-nums">
        {fmt(row.effectiveWorkRvu)}
        {row.overrideWorkRvu != null ? (
          <span
            className="ml-1 text-[color:var(--color-accent)]"
            title="Effective value comes from a manual override"
          >
            *
          </span>
        ) : null}
      </div>
      <div role="cell" className="px-4 py-2.5">
        <OverrideCell
          row={row}
          saving={saving}
          onSave={(v) => onSave(row.code, v)}
          onClear={() => onClear(row.code)}
          onEditingChange={onEditingChange}
        />
        {siteCount > 0 ? (
          <button
            type="button"
            onClick={() => onToggleExpand(row.code)}
            className="mt-1.5 block rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-accent)]/50"
            title="Show the per-site overrides"
          >
            <Badge variant="accent">
              {siteCount} site{siteCount === 1 ? "" : "s"}
            </Badge>
          </button>
        ) : null}
      </div>
    </div>
  );
});

// An expanded code's per-site override row: the site, its work RVU (inline-editable),
// and a clear button. Indented under the code row.
const SiteOverrideRow = memo(function SiteOverrideRow({
  code,
  ov,
  saving,
  onSave,
  onClear,
  onEditing,
}: {
  code: string;
  ov: RvuOverride;
  saving: boolean;
  onSave: (code: string, siteCode: string, value: number) => void;
  onClear: (code: string, siteCode: string) => void;
  onEditing: (key: string, active: boolean) => void;
}) {
  const site = ov.siteCode ?? "";
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(ov.overrideWorkRvu.toString());
  const inputRef = useRef<HTMLInputElement | null>(null);

  useEffect(() => {
    if (!editing) setDraft(ov.overrideWorkRvu.toString());
  }, [ov.overrideWorkRvu, editing]);
  useEffect(() => {
    if (editing) inputRef.current?.focus();
  }, [editing]);
  // Report edit state up so the virtualizer keeps this row mounted (draft survives scroll).
  useEffect(() => {
    onEditing(`${code}::${site}`, editing);
  }, [editing, onEditing, code, site]);

  function commit() {
    const t = draft.trim();
    setEditing(false);
    if (t === "") return;
    const n = Number(t);
    if (!Number.isFinite(n) || n < 0) return;
    if (n === ov.overrideWorkRvu) return;
    onSave(code, site, n);
  }

  return (
    <div className="flex items-center gap-3 border-t border-[color:var(--color-border)]/50 bg-[color:var(--color-surface-2)]/30 py-2 pl-12 pr-4 text-sm">
      <span className="text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)]">
        Site
      </span>
      <Badge variant="neutral" title="Novarad site this override applies to">{site}</Badge>
      <div className="ml-auto flex items-center gap-2">
        {editing ? (
          <span className="inline-flex items-center gap-1">
            <input
              ref={inputRef}
              type="number"
              step="0.01"
              min={0}
              value={draft}
              onChange={(e) => setDraft(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter") {
                  e.preventDefault();
                  commit();
                } else if (e.key === "Escape") {
                  e.preventDefault();
                  setEditing(false);
                }
              }}
              aria-label={`Override work RVU for ${code} at ${site}`}
              className="w-24 rounded border border-[color:var(--color-accent)]/60 bg-[color:var(--color-surface)] px-2 py-1 text-sm text-right tabular-nums focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-accent)]/60"
            />
            <button
              type="button"
              onMouseDown={(e) => e.preventDefault()}
              onClick={commit}
              className="text-[color:var(--color-accent)] hover:opacity-80"
              aria-label="Save"
            >
              <Check className="size-4" />
            </button>
            <button
              type="button"
              onMouseDown={(e) => e.preventDefault()}
              onClick={() => setEditing(false)}
              className="text-[color:var(--color-muted-fg)] hover:opacity-80"
              aria-label="Cancel"
            >
              <X className="size-4" />
            </button>
          </span>
        ) : (
          <span className="inline-flex items-center gap-2 font-mono tabular-nums">
            <Badge
              variant="accent"
              title="Site-specific override (wins over the tenant-wide value at this site)"
            >
              {fmt(ov.overrideWorkRvu)}
            </Badge>
            <button
              type="button"
              onClick={() => setEditing(true)}
              disabled={saving}
              className="text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-accent)] disabled:opacity-50"
              aria-label={`Edit override for ${code} at ${site}`}
            >
              <Pencil className="size-3.5" />
            </button>
            <button
              type="button"
              onClick={() => onClear(code, site)}
              disabled={saving}
              className="text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-accent)] disabled:opacity-50"
              aria-label={`Clear override for ${code} at ${site}`}
            >
              <X className="size-4" />
            </button>
            {saving ? <Spinner size={12} /> : null}
          </span>
        )}
      </div>
    </div>
  );
});

// The "add a site override" row under an expanded code. Offers only the sites where
// the code is actually billed (latest reconciliation run), minus any already overridden.
function AddSiteRow({
  code,
  existingSites,
  pendingSiteCode,
  onSave,
}: {
  code: string;
  existingSites: string[];
  // The site whose site-override save is in flight for this code, or null. Busy only
  // when it's a NEW site (an edit of an existing site row shouldn't spin this control).
  pendingSiteCode: string | null;
  onSave: (code: string, siteCode: string, value: number) => void;
}) {
  const saving = pendingSiteCode != null && !existingSites.includes(pendingSiteCode);
  // All of the tenant's Novarad sites (the facilities), so any code can be overridden at
  // any site — not gated on reconciliation activity. Shared cache with the unmapped picker.
  const sites = useQuery({
    queryKey: ["billing-sites"],
    queryFn: () => billingApi.listSites(),
    staleTime: 5 * 60_000,
  });
  const available = (sites.data ?? []).filter((s) => !existingSites.includes(s));
  const [site, setSite] = useState("");
  const [val, setVal] = useState("");

  function add() {
    if (!site) return;
    const n = Number(val.trim());
    if (!Number.isFinite(n) || n < 0) return;
    onSave(code, site, n);
    setSite("");
    setVal("");
  }

  return (
    <div className="flex flex-wrap items-center gap-2 border-t border-[color:var(--color-border)]/50 bg-[color:var(--color-surface-2)]/30 py-2 pl-12 pr-4 text-sm">
      <Plus className="size-3.5 text-[color:var(--color-muted-fg)]" />
      {sites.isLoading ? (
        <span className="inline-flex items-center gap-2 text-[color:var(--color-muted-fg)]">
          <Spinner size={12} /> Loading sites…
        </span>
      ) : sites.isError ? (
        <span className="text-xs text-[color:var(--color-novarad-red)]">
          Couldn&apos;t load the site list.{" "}
          <button type="button" className="underline underline-offset-2" onClick={() => sites.refetch()}>
            Try again
          </button>
        </span>
      ) : available.length === 0 ? (
        <span className="text-xs text-[color:var(--color-muted-fg)]">
          Every site already has an override for this code.
        </span>
      ) : (
        <>
          <label className="text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)]">
            Add site
          </label>
          <select
            value={site}
            onChange={(e) => setSite(e.target.value)}
            aria-label={`Site for a new override of ${code}`}
            className="h-9 rounded-md border border-[color:var(--color-border)] bg-[color:var(--color-surface)] px-2 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-accent)]/60"
          >
            <option value="">Choose a site…</option>
            {available.map((s) => (
              <option key={s} value={s}>
                {s}
              </option>
            ))}
          </select>
          <input
            type="number"
            step="0.01"
            min={0}
            value={val}
            onChange={(e) => setVal(e.target.value)}
            placeholder="work RVU"
            aria-label={`Override work RVU for ${code} at the chosen site`}
            className="w-28 rounded-md border border-[color:var(--color-border)] bg-[color:var(--color-surface)] px-2 py-1 text-sm text-right tabular-nums focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-accent)]/60"
          />
          <Button
            variant="secondary"
            size="sm"
            onClick={add}
            disabled={saving || !site || val.trim() === ""}
          >
            {saving ? <Spinner size={12} /> : "Add override"}
          </Button>
        </>
      )}
    </div>
  );
}

function CmsCheckBadge({ row }: { row: CptMasterCmsRow }) {
  switch (row.verdict) {
    case "matches":
      return (
        <Badge variant="success" title={`CMS work RVU = ${fmt(row.cmsWorkRvu)}`}>
          = CMS
        </Badge>
      );
    case "matches_sum":
      return (
        <Badge
          variant="success"
          title={`Master = sum of ${row.bundleMatched} CMS components (${fmt(row.cmsWorkRvu)})`}
        >
          = Σ CMS ({row.bundleMatched})
        </Badge>
      );
    case "differs":
      return (
        <Badge variant="accent" title="Master RVU differs from CMS; worth a review">
          CMS {fmt(row.cmsWorkRvu)}
        </Badge>
      );
    case "differs_sum":
      return (
        <Badge variant="accent" title="Master bundle RVU differs from the CMS component sum; worth a review">
          Σ CMS {fmt(row.cmsWorkRvu)}
        </Badge>
      );
    case "partial":
      return (
        <Badge variant="accent" title="Not all bundle components are in CMS, so it can't be fully validated">
          partial {row.bundleMatched}/{row.bundleParts}
        </Badge>
      );
    case "status_gated":
      return (
        <Badge
          variant="neutral"
          title={`CMS status ${row.cmsStatus}: not separately payable, so the master value is used`}
        >
          status {row.cmsStatus}
        </Badge>
      );
    case "not_in_cms":
    default:
      return (
        <Badge variant="neutral" title="No CMS PPRRVU row for this code">
          not in CMS
        </Badge>
      );
  }
}

function OverrideCell({
  row,
  saving,
  onSave,
  onClear,
  onEditingChange,
}: {
  row: CptMasterCmsRow;
  saving: boolean;
  onSave: (value: number) => void;
  onClear: () => void;
  onEditingChange?: (active: boolean) => void;
}) {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(row.overrideWorkRvu?.toString() ?? "");
  const inputRef = useRef<HTMLInputElement | null>(null);

  useEffect(() => {
    if (!editing) setDraft(row.overrideWorkRvu?.toString() ?? "");
  }, [row.overrideWorkRvu, editing]);

  useEffect(() => {
    if (editing) inputRef.current?.focus();
  }, [editing]);
  // Report edit state up so the virtualizer keeps this row mounted while editing.
  useEffect(() => {
    onEditingChange?.(editing);
  }, [editing, onEditingChange]);

  function commit() {
    const trimmed = draft.trim();
    setEditing(false);
    // An empty field is a cancel, not a 0 — Number("") is 0, which would otherwise
    // silently persist a 0-RVU override. Clearing is the explicit X button only.
    if (trimmed === "") return;
    const parsed = Number(trimmed);
    if (!Number.isFinite(parsed) || parsed < 0) return;
    if (parsed === row.overrideWorkRvu) return;
    onSave(parsed);
  }

  if (editing) {
    return (
      <span className="inline-flex items-center gap-1">
        <input
          ref={inputRef}
          type="number"
          step="0.01"
          min={0}
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              e.preventDefault();
              commit();
            } else if (e.key === "Escape") {
              e.preventDefault();
              setEditing(false);
            }
          }}
          aria-label={`Override work RVU for ${row.code}`}
          className="w-24 rounded border border-[color:var(--color-accent)]/60 bg-[color:var(--color-surface)] px-2 py-1 text-sm text-right tabular-nums focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-accent)]/60"
        />
        <button
          type="button"
          onMouseDown={(e) => e.preventDefault()}
          onClick={commit}
          className="text-[color:var(--color-accent)] hover:opacity-80"
          aria-label="Save override"
        >
          <Check className="size-4" />
        </button>
        <button
          type="button"
          onMouseDown={(e) => e.preventDefault()}
          onClick={() => setEditing(false)}
          className="text-[color:var(--color-muted-fg)] hover:opacity-80"
          aria-label="Cancel"
        >
          <X className="size-4" />
        </button>
      </span>
    );
  }

  if (row.overrideWorkRvu != null) {
    return (
      <span className="inline-flex items-center gap-2">
        <Badge variant="accent" title="Manual tenant-wide override">
          {fmt(row.overrideWorkRvu)}
        </Badge>
        <button
          type="button"
          onClick={() => setEditing(true)}
          disabled={saving}
          className="text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-accent)] disabled:opacity-50"
          aria-label={`Edit override for ${row.code}`}
        >
          <Pencil className="size-3.5" />
        </button>
        <button
          type="button"
          onClick={onClear}
          disabled={saving}
          className="text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-accent)] disabled:opacity-50"
          aria-label={`Clear override for ${row.code}`}
        >
          <X className="size-4" />
        </button>
        {saving ? <Spinner size={12} /> : null}
      </span>
    );
  }

  return (
    <span className="inline-flex items-center gap-2">
      <Button
        variant="ghost"
        size="sm"
        onClick={() => setEditing(true)}
        disabled={saving}
      >
        Set override
      </Button>
      {saving ? <Spinner size={12} /> : null}
    </span>
  );
}

// Inline edit for the master row's description / work RVU (merged from the former
// CPT master page). Click to edit; Enter/blur commits, Escape cancels.
function EditableCell({
  value,
  type,
  ariaLabel,
  saving,
  align = "left",
  onSave,
  onEditingChange,
}: {
  value: string;
  type: "text" | "number";
  ariaLabel: string;
  saving: boolean;
  align?: "left" | "right";
  onSave: (next: string) => void;
  onEditingChange?: (active: boolean) => void;
}) {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(value);
  const inputRef = useRef<HTMLInputElement | null>(null);

  useEffect(() => {
    if (!editing) setDraft(value);
  }, [value, editing]);
  useEffect(() => {
    if (editing) inputRef.current?.focus();
  }, [editing]);
  // Report edit state up so the virtualizer keeps this row mounted while editing.
  useEffect(() => {
    onEditingChange?.(editing);
  }, [editing, onEditingChange]);

  function commit() {
    setEditing(false);
    onSave(draft.trim());
  }
  function cancel() {
    setDraft(value);
    setEditing(false);
  }

  if (!editing) {
    return (
      <button
        type="button"
        onClick={() => setEditing(true)}
        disabled={saving}
        className={cn(
          "block w-full -mx-1 px-1 py-0.5 rounded text-left hover:bg-[color:var(--color-surface-2)] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-accent)]/50",
          align === "right" && "text-right",
        )}
        aria-label={`Edit ${ariaLabel}`}
      >
        {value || <span className="text-[color:var(--color-muted-fg)] italic">empty</span>}
        {saving ? (
          <span className="ml-2 inline-block align-middle">
            <Spinner size={10} />
          </span>
        ) : null}
      </button>
    );
  }

  return (
    <span className="inline-flex items-center gap-1 w-full">
      <input
        ref={inputRef}
        type={type}
        step={type === "number" ? "0.01" : undefined}
        min={type === "number" ? 0 : undefined}
        value={draft}
        onChange={(e) => setDraft(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === "Enter") {
            e.preventDefault();
            commit();
          } else if (e.key === "Escape") {
            e.preventDefault();
            cancel();
          }
        }}
        onBlur={commit}
        aria-label={ariaLabel}
        className={cn(
          "flex-1 min-w-0 rounded border border-[color:var(--color-accent)]/60 bg-[color:var(--color-surface)] px-2 py-1 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-accent)]/60",
          align === "right" && "text-right tabular-nums",
        )}
      />
      <button
        type="button"
        onMouseDown={(e) => e.preventDefault()}
        onClick={commit}
        className="text-[color:var(--color-accent)] hover:opacity-80"
        aria-label="Save"
      >
        <Check className="size-4" />
      </button>
      <button
        type="button"
        onMouseDown={(e) => e.preventDefault()}
        onClick={cancel}
        className="text-[color:var(--color-muted-fg)] hover:opacity-80"
        aria-label="Cancel"
      >
        <X className="size-4" />
      </button>
    </span>
  );
}

function ErrorBar({ label, onRetry }: { label: string; onRetry: () => void }) {
  return (
    <div className="mb-3 rounded-md border border-[color:var(--color-novarad-red)]/40 bg-[color:var(--color-novarad-red)]/10 px-3 py-3 text-sm text-[color:var(--color-novarad-red)] flex items-center gap-2">
      <AlertCircle className="size-4" />
      {label}
      <button className="underline underline-offset-2" onClick={onRetry}>
        Try again
      </button>
    </div>
  );
}
