"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  AlertCircle,
  AlertTriangle,
  ArrowRight,
  CheckCircle2,
  Database,
  RefreshCw,
} from "lucide-react";
import { useEffect, useMemo, useState } from "react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Spinner } from "@/components/ui/spinner";
import { billingApi } from "@/lib/api";
import type { MModalIssuer, RvuQuarter, RvuSyncPreview, RvuSyncRun } from "@/lib/types";
import { formatDateTime } from "@/lib/utils";

const QUARTERS: RvuQuarter[] = ["A", "B", "C", "D"];
const QUARTER_ORDINAL: Record<RvuQuarter, string> = { A: "1st", B: "2nd", C: "3rd", D: "4th" };
const MAX_DIFF_ROWS = 500;
const ALL = "__all__"; // sentinel scope value = every issuer
const fmt = (n: number | null | undefined) => (n == null ? "—" : n.toFixed(2));
const num = (n: number) => n.toLocaleString();

/**
 * "Sync to M*Modal" — pushes the effective work RVUs for a (year, quarter) out to the
 * M*Modal ClinicalDataStore. The operator picks WHICH issuer (facility) to sync — one at a
 * time by default, or, deliberately, all of them (with a hard warning). Diff-only: preview
 * the before→after, then apply only the codes that changed. Self-gates on a configured
 * connection. NRS/Admin only (enforced server-side).
 */
export function MModalSyncPanel({ year }: { year: number }) {
  const qc = useQueryClient();
  const [quarter, setQuarter] = useState<RvuQuarter>("A");
  const [scope, setScope] = useState<string>(""); // "" until issuers load, then an issuerKey or ALL
  const [confirmAll, setConfirmAll] = useState(false);
  const [preview, setPreview] = useState<RvuSyncPreview | null>(null);

  const status = useQuery({
    queryKey: ["mmodal-sync-status"],
    queryFn: () => billingApi.rvuSyncStatus(),
  });
  const configured = status.data?.configured ?? false;

  const issuers = useQuery({
    queryKey: ["mmodal-issuers"],
    queryFn: () => billingApi.listSyncIssuers(),
    enabled: configured,
    staleTime: 5 * 60_000,
  });
  const runs = useQuery({
    queryKey: ["mmodal-sync-runs"],
    queryFn: () => billingApi.listRvuSyncRuns(10),
  });

  // Preselect the connection's default issuer (or the biggest) once the list loads.
  useEffect(() => {
    if (scope || !issuers.data || issuers.data.length === 0) return;
    const def = issuers.data.find((i) => i.isDefault) ?? issuers.data[0];
    setScope(def.issuerKey);
  }, [issuers.data, scope]);

  const isAll = scope === ALL;
  const apiScope = isAll ? { allIssuers: true } : { issuerKey: scope };

  const previewMut = useMutation({
    mutationFn: () => billingApi.rvuSyncPreview(year, quarter, apiScope),
    onSuccess: (p) => setPreview(p),
  });
  const applyMut = useMutation({
    mutationFn: () => billingApi.rvuSyncApply(year, quarter, apiScope),
    onSuccess: () => {
      setPreview(null);
      qc.invalidateQueries({ queryKey: ["mmodal-sync-status"] });
      qc.invalidateQueries({ queryKey: ["mmodal-sync-runs"] });
    },
  });

  // A computed diff is stale the moment the snapshot (year/quarter) or target issuer changes.
  useEffect(() => {
    setPreview(null);
    setConfirmAll(false);
    applyMut.reset();
    previewMut.reset();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [year, quarter, scope]);

  // issuerKey -> name, for the run-history display.
  const issuerName = useMemo(() => {
    const m = new Map<string, string>();
    for (const i of issuers.data ?? []) m.set(i.issuerKey, i.name);
    return (key: string | null) => (key == null ? "All issuers" : m.get(key) ?? "one facility");
  }, [issuers.data]);

  if (status.isLoading) {
    return (
      <div className="min-h-[30vh] flex items-center justify-center">
        <Spinner size={24} />
      </div>
    );
  }

  const selectedIssuer = issuers.data?.find((i) => i.issuerKey === scope);
  const canApply =
    !!preview && preview.updatable > 0 && (!isAll || confirmAll);

  return (
    <div className="space-y-6">
      <div className="flex items-start gap-3">
        <Database className="size-5 mt-0.5 text-[color:var(--color-accent)]" />
        <div>
          <h2 className="text-lg" style={{ fontFamily: "var(--font-display)" }}>
            Sync to M*Modal
          </h2>
          <p className="text-sm text-[color:var(--color-muted-fg)] mt-1 max-w-2xl">
            Push the effective work RVUs reconciliation credits (override → CMS → master,
            status-A only) out to the M*Modal dictation system. Choose a facility, preview the
            changes, then apply — only codes whose RVU actually differs are written.
          </p>
        </div>
      </div>

      {!configured ? (
        <div className="rounded-md border border-[color:var(--color-accent)]/30 bg-[color:var(--color-accent)]/10 px-4 py-4">
          <div className="flex items-center gap-2 text-sm font-medium">
            <AlertCircle className="size-4 text-[color:var(--color-accent)]" />
            M*Modal write-back isn&apos;t configured for this tenant yet.
          </div>
          <p className="text-sm text-[color:var(--color-muted-fg)] mt-1 ml-6">
            Once a ClinicalDataStore connection is set up, you&apos;ll be able to push RVUs
            from here. Nothing is written until then.
          </p>
        </div>
      ) : (
        <>
          {/* Controls */}
          <div className="flex flex-wrap items-end gap-3">
            <div className="flex flex-col gap-1">
              <label className="text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)]">
                Facility (issuer)
              </label>
              <IssuerSelect
                value={scope}
                onChange={setScope}
                issuers={issuers.data ?? []}
                loading={issuers.isLoading}
              />
            </div>
            <div className="flex flex-col gap-1">
              <label className="text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)]">
                Snapshot quarter
              </label>
              <select
                value={quarter}
                onChange={(e) => setQuarter(e.target.value as RvuQuarter)}
                aria-label="Snapshot quarter for the sync run"
                className="h-10 rounded-md border border-[color:var(--color-border)] bg-[color:var(--color-surface)] px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-accent)]/60"
              >
                {QUARTERS.map((q) => (
                  <option key={q} value={q}>
                    {QUARTER_ORDINAL[q]} quarter ({year})
                  </option>
                ))}
              </select>
            </div>
            <Button
              variant="secondary"
              loading={previewMut.isPending}
              disabled={!scope}
              onClick={() => previewMut.mutate()}
            >
              <RefreshCw className="size-4" />
              Preview changes
            </Button>
            <Button
              variant="primary"
              loading={applyMut.isPending}
              disabled={!canApply}
              onClick={() => applyMut.mutate()}
            >
              <ArrowRight className="size-4" />
              {preview && preview.updatable > 0
                ? `Apply ${preview.updatable} change${preview.updatable === 1 ? "" : "s"}`
                : "Apply changes"}
            </Button>
          </div>

          {/* Context line for the selected scope */}
          {!isAll && selectedIssuer ? (
            <p className="text-xs text-[color:var(--color-muted-fg)]">
              Targeting <strong className="text-[color:var(--color-base-fg)]">{selectedIssuer.name}</strong>
              {selectedIssuer.description ? ` — ${selectedIssuer.description}` : ""} ·{" "}
              {num(selectedIssuer.activeCodeCount)} active exam codes.
            </p>
          ) : null}

          {/* All-issuers hard warning + confirmation */}
          {isAll ? (
            <div className="rounded-md border border-[color:var(--color-caution)]/50 bg-[color:var(--color-caution)]/10 px-4 py-3">
              <div className="flex items-center gap-2 text-sm font-medium text-[color:var(--color-caution)]">
                <AlertTriangle className="size-4" />
                This targets every facility ({num((issuers.data ?? []).length)} issuers).
              </div>
              <p className="text-sm text-[color:var(--color-muted-fg)] mt-1 ml-6">
                Applying will overwrite matching RVUs at <strong>all</strong> facilities that
                share this M*Modal instance — including other customers&apos; exam codes. Preview
                is safe; only confirm below if you&apos;re certain you want to write to all of them.
              </p>
              <label className="mt-2 ml-6 flex items-center gap-2 text-sm cursor-pointer select-none">
                <input
                  type="checkbox"
                  checked={confirmAll}
                  onChange={(e) => setConfirmAll(e.target.checked)}
                  className="size-4 accent-[color:var(--color-caution)]"
                />
                I understand this affects all {num((issuers.data ?? []).length)} facilities.
              </label>
            </div>
          ) : null}

          {previewMut.isError ? (
            <p className="text-sm text-[color:var(--color-novarad-red)]">
              Couldn&apos;t reach M*Modal to compute the diff. Try again, or check the
              connection if it keeps failing.
            </p>
          ) : null}

          {applyMut.isError ? (
            <p className="text-sm text-[color:var(--color-novarad-red)]">
              The sync failed and was rolled back — nothing was written. Try again, or check
              the connection if it keeps failing.
            </p>
          ) : null}

          {applyMut.data?.success ? (
            <div className="rounded-md border border-[oklch(0.72_0.14_160)]/40 bg-[oklch(0.72_0.14_160)]/10 px-4 py-3">
              <div className="flex items-center gap-2 text-sm font-medium">
                <CheckCircle2 className="size-4 text-[oklch(0.72_0.14_160)]" />
                Pushed {applyMut.data.updated} RVU{applyMut.data.updated === 1 ? "" : "s"} to
                M*Modal for {applyMut.data.year}
                {applyMut.data.quarter}.
              </div>
              <div className="text-xs text-[color:var(--color-muted-fg)] mt-1 ml-6 flex flex-wrap gap-x-4">
                <span>{applyMut.data.unchanged} already matched</span>
                {applyMut.data.missing > 0 ? (
                  <span>{applyMut.data.missing} not found in M*Modal</span>
                ) : null}
              </div>
            </div>
          ) : null}

          {preview ? <PreviewPanel preview={preview} /> : null}
        </>
      )}

      <SyncHistory runs={runs.data ?? []} loading={runs.isLoading} issuerName={issuerName} />
    </div>
  );
}

function IssuerSelect({
  value,
  onChange,
  issuers,
  loading,
}: {
  value: string;
  onChange: (v: string) => void;
  issuers: MModalIssuer[];
  loading: boolean;
}) {
  if (loading) {
    return (
      <span className="inline-flex h-10 items-center gap-2 text-sm text-[color:var(--color-muted-fg)]">
        <Spinner size={14} /> Loading facilities…
      </span>
    );
  }
  return (
    <select
      value={value}
      onChange={(e) => onChange(e.target.value)}
      aria-label="Facility (M*Modal issuer) to sync"
      className="h-10 min-w-64 rounded-md border border-[color:var(--color-border)] bg-[color:var(--color-surface)] px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-accent)]/60"
    >
      {value === "" ? <option value="">Choose a facility…</option> : null}
      {issuers.map((i) => (
        <option key={i.issuerKey} value={i.issuerKey}>
          {i.name}
          {i.isDefault ? " (default)" : ""} — {num(i.activeCodeCount)} codes
        </option>
      ))}
      <option value={ALL}>⚠ All issuers — {num(issuers.length)} facilities</option>
    </select>
  );
}

function PreviewPanel({ preview }: { preview: RvuSyncPreview }) {
  // Surface only the actionable rows (changes + misses); "unchanged" is a count.
  const actionable = preview.diffs
    .filter((d) => d.action !== "unchanged")
    .sort((a, b) => (a.action === b.action ? a.hcpcs.localeCompare(b.hcpcs) : a.action === "update" ? -1 : 1));
  const shown = actionable.slice(0, MAX_DIFF_ROWS);

  return (
    <div className="rounded-lg border border-[color:var(--color-border)] bg-[color:var(--color-surface)]">
      <div className="flex flex-wrap items-center gap-x-4 gap-y-1 px-4 py-3 border-b border-[color:var(--color-border)] text-sm">
        <span className="font-medium">{preview.total} effective codes</span>
        <span className="text-[color:var(--color-accent)]">
          <strong>{preview.updatable}</strong> to update
        </span>
        <span className="text-[color:var(--color-muted-fg)]">{preview.unchanged} unchanged</span>
        {preview.missing > 0 ? (
          <span className="text-[color:var(--color-muted-fg)]">{preview.missing} not in M*Modal</span>
        ) : null}
      </div>

      {actionable.length === 0 ? (
        <p className="px-4 py-6 text-sm text-[color:var(--color-muted-fg)]">
          Everything already matches — nothing to push.
        </p>
      ) : (
        <div className="overflow-x-auto">
          <div className="min-w-[34rem]">
            <div className="grid [grid-template-columns:10rem_8rem_2rem_8rem_8rem] gap-x-2 px-4 py-2 text-[10px] uppercase tracking-[0.14em] text-[color:var(--color-muted-fg)] border-b border-[color:var(--color-border)]">
              <span>Code</span>
              <span className="text-right">M*Modal now</span>
              <span />
              <span className="text-right">New</span>
              <span className="text-right">Status</span>
            </div>
            {shown.map((d) => (
              <div
                key={d.hcpcs}
                className="grid [grid-template-columns:10rem_8rem_2rem_8rem_8rem] gap-x-2 px-4 py-1.5 items-center border-b border-[color:var(--color-border)]/50 text-sm font-mono tabular-nums"
              >
                <span>{d.hcpcs}</span>
                <span className="text-right text-[color:var(--color-muted-fg)]">{fmt(d.currentRvu)}</span>
                <ArrowRight className="size-3 text-[color:var(--color-muted-fg)]" />
                <span className="text-right">{fmt(d.newRvu)}</span>
                <span className="text-right">
                  {d.action === "update" ? (
                    <Badge variant="accent">update</Badge>
                  ) : (
                    <Badge variant="caution" title="No active M*Modal row for this code">
                      no match
                    </Badge>
                  )}
                </span>
              </div>
            ))}
            {actionable.length > shown.length ? (
              <p className="px-4 py-2 text-xs text-[color:var(--color-muted-fg)]">
                Showing the first {shown.length} of {actionable.length}. Applying writes all
                of them.
              </p>
            ) : null}
          </div>
        </div>
      )}
    </div>
  );
}

function SyncHistory({
  runs,
  loading,
  issuerName,
}: {
  runs: RvuSyncRun[];
  loading: boolean;
  issuerName: (key: string | null) => string;
}) {
  return (
    <div>
      <h3 className="text-sm font-medium text-[color:var(--color-muted-fg)] mb-2">
        Recent syncs
      </h3>
      {loading ? (
        <Spinner size={18} />
      ) : runs.length === 0 ? (
        <p className="text-sm text-[color:var(--color-muted-fg)]">No syncs yet.</p>
      ) : (
        <div className="rounded-lg border border-[color:var(--color-border)] bg-[color:var(--color-surface)] divide-y divide-[color:var(--color-border)]/60">
          {runs.map((r) => (
            <div key={r.syncRunId} className="flex flex-wrap items-center gap-x-4 gap-y-1 px-4 py-2 text-sm">
              <span className="text-[color:var(--color-muted-fg)] tabular-nums w-44">
                {formatDateTime(r.ranAt)}
              </span>
              <span className="font-mono">
                {r.year}
                {r.quarter}
              </span>
              <span
                className={
                  r.issuerKey == null
                    ? "text-[color:var(--color-caution)]"
                    : "text-[color:var(--color-base-fg)]"
                }
              >
                {issuerName(r.issuerKey)}
              </span>
              {r.success ? (
                <Badge variant="success">{r.updatedRows} updated</Badge>
              ) : (
                <Badge variant="danger">failed</Badge>
              )}
              <span className="text-xs text-[color:var(--color-muted-fg)]">
                {r.unchangedRows} unchanged
                {r.missingRows > 0 ? ` · ${r.missingRows} not found` : ""}
              </span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
