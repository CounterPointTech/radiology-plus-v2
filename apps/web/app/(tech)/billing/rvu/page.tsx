"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AlertCircle, Check, ExternalLink, Pencil, Search, X } from "lucide-react";
import { useRouter } from "next/navigation";
import { useEffect, useMemo, useRef, useState } from "react";

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

type Tab = "cms" | "master";

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
            RVU values
          </h1>
          <p className="text-sm text-[color:var(--color-muted-fg)] mt-1">
            The CMS PFS relative-value source of truth, and how the CPT master
            reconciles against it.
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
      </div>

      {tab === "master" ? (
        <MasterOverridesTab year={year} />
      ) : (
        <CmsValuesTab year={year} imports={imports.data ?? []} />
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

  const check = useQuery({
    queryKey: ["cms-check", year],
    queryFn: () => billingApi.cptMasterCmsCheck({ year, limit: MASTER_LIMIT }),
  });

  const upsert = useMutation({
    mutationFn: (v: { code: string; overrideWorkRvu: number }) =>
      billingApi.upsertRvuOverride(v.code, { year, overrideWorkRvu: v.overrideWorkRvu }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["cms-check", year] }),
  });

  const clear = useMutation({
    mutationFn: (code: string) => billingApi.deleteRvuOverride(code, year),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["cms-check", year] }),
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

  const pendingCode =
    (upsert.isPending && upsert.variables?.code) ||
    (clear.isPending && clear.variables) ||
    null;

  return (
    <>
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
      </p>

      {check.isError ? (
        <ErrorBar label="Couldn't load the CPT master / CMS check." onRetry={() => check.refetch()} />
      ) : null}

      {upsert.isError || clear.isError ? (
        <p className="mb-3 text-sm text-[color:var(--color-novarad-red)]">
          Couldn&apos;t save that override. Try again, or refresh if it keeps failing.
        </p>
      ) : null}

      <div className="rounded-lg border border-[color:var(--color-border)] bg-[color:var(--color-surface)] overflow-hidden">
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead className="text-left text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)] bg-[color:var(--color-surface-2)]">
              <tr>
                <th className="px-4 py-3 font-medium w-52 cursor-help" title="The CPT code, or a semicolon-separated multi-code bundle, from the master sheet.">Code</th>
                <th className="px-4 py-3 font-medium cursor-help" title="Procedure description from the CPT master.">Description</th>
                <th className="px-4 py-3 font-medium text-right w-24 cursor-help" title="Work RVU from the imported CPT master sheet (this site's curated source).">Master</th>
                <th className="px-4 py-3 font-medium w-44 cursor-help" title="How the master RVU compares to CMS: a single code against the CMS work RVU, or a bundle against the sum of its CMS component work RVUs.">CMS check</th>
                <th className="px-4 py-3 font-medium text-right w-24 cursor-help" title="The work RVU reconciliation actually credits: the override if set, otherwise CMS, otherwise the master value.">Effective</th>
                <th className="px-4 py-3 font-medium w-48 cursor-help" title="A manual tenant-wide work-RVU override that wins over CMS and the master. Set, edit, or clear it here.">Override</th>
              </tr>
            </thead>
            <tbody>
              {check.isLoading ? (
                <tr>
                  <td colSpan={6} className="px-4 py-10 text-center text-[color:var(--color-muted-fg)]">
                    <Spinner size={20} />
                  </td>
                </tr>
              ) : rows.length === 0 && !check.isError ? (
                <tr>
                  <td colSpan={6} className="px-4 py-10 text-center text-sm text-[color:var(--color-muted-fg)]">
                    {onlyNonReconciling
                      ? "Every code reconciles to CMS for this filter. 🎉"
                      : debouncedSearch
                        ? `No codes matching "${debouncedSearch}" in ${year}.`
                        : `No CPT master rows for ${year}.`}
                  </td>
                </tr>
              ) : (
                rows.map((r) => (
                  <MasterRow
                    key={r.code}
                    row={r}
                    saving={pendingCode === r.code}
                    onSave={(v) => upsert.mutate({ code: r.code, overrideWorkRvu: v })}
                    onClear={() => clear.mutate(r.code)}
                  />
                ))
              )}
            </tbody>
          </table>
        </div>
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

function MasterRow({
  row,
  saving,
  onSave,
  onClear,
}: {
  row: CptMasterCmsRow;
  saving: boolean;
  onSave: (value: number) => void;
  onClear: () => void;
}) {
  return (
    <tr className="border-t border-[color:var(--color-border)] hover:bg-[color:var(--color-surface-2)]/50 align-top">
      <td className="px-4 py-2.5 font-mono text-xs">
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
      </td>
      <td className="px-4 py-2.5 max-w-sm">
        <span className="line-clamp-2" title={row.description}>
          {row.description}
        </span>
      </td>
      <td className="px-4 py-2.5 font-mono text-right tabular-nums">{fmt(row.masterWorkRvu)}</td>
      <td className="px-4 py-2.5">
        <CmsCheckBadge row={row} />
      </td>
      <td className="px-4 py-2.5 font-mono text-right tabular-nums">
        {fmt(row.effectiveWorkRvu)}
        {row.overrideWorkRvu != null ? (
          <span
            className="ml-1 text-[color:var(--color-accent)]"
            title="Effective value comes from a manual override"
          >
            *
          </span>
        ) : null}
      </td>
      <td className="px-4 py-2.5">
        <OverrideCell row={row} saving={saving} onSave={onSave} onClear={onClear} />
      </td>
    </tr>
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
}: {
  row: CptMasterCmsRow;
  saving: boolean;
  onSave: (value: number) => void;
  onClear: () => void;
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
