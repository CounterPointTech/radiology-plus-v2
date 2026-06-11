"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AxiosError } from "axios";
import {
  AlertCircle,
  ChevronDown,
  ChevronRight,
  Download,
  Pause,
  Pencil,
  Play,
  Search,
  Upload,
  Wand2,
} from "lucide-react";
import { useEffect, useState } from "react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardBody, CardHeader, CardTitle } from "@/components/ui/card";
import { Dialog, DialogActions } from "@/components/ui/dialog";
import { Input, Label, Textarea } from "@/components/ui/input";
import { Spinner } from "@/components/ui/spinner";
import { billingApi } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import {
  canAccessBilling,
  type BulkImportResult,
  type BulkImportRow,
  type CrosswalkStatus,
  type CrosswalkSuggestion,
  type ServiceCodeMapping,
  type UnmappedServiceCode,
} from "@/lib/types";

function localIso(d: Date): string {
  const y = d.getFullYear();
  const m = String(d.getMonth() + 1).padStart(2, "0");
  const day = String(d.getDate()).padStart(2, "0");
  return `${y}-${m}-${day}`;
}

function errMessage(err: unknown, fallback: string): string {
  const ax = err as AxiosError<{ error?: string }>;
  return ax?.response?.data?.error ?? fallback;
}

function formatRelative(iso: string | null): string {
  if (!iso) return "—";
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "—";
  return d.toLocaleString();
}

// RFC-4180 field escaping: quote when the value contains a comma, quote, or
// newline; double any embedded quotes. Matches what the bulk-import parser reads.
function csvField(value: string | number | null): string {
  const s = value == null ? "" : String(value);
  return /[",\r\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
}

// Build a crosswalk-ready CSV from the unmapped report. The header carries the
// service_code/cpt_code/note columns the bulk importer reads (cpt_code blank for
// the user to fill); the type/description/reports/lines columns are context the
// importer ignores but that help the user prioritize while filling in CPTs.
function buildUnmappedCsv(codes: UnmappedServiceCode[]): string {
  const header = ["service_code", "cpt_code", "type", "description", "reports", "lines", "note"];
  const lines = [header.join(",")];
  for (const c of codes) {
    lines.push(
      [
        csvField(c.code),
        "", // cpt_code — left blank for the user to fill, then re-import
        csvField(c.kind === "cpt_missing_from_master" ? "Missing CPT" : "Non-CPT"),
        csvField(c.description),
        csvField(c.reportCount),
        csvField(c.serviceLineCount),
        "", // note — optional
      ].join(","),
    );
  }
  // Trailing newline so editors/round-trips don't merge the last row.
  return lines.join("\r\n") + "\r\n";
}

function downloadCsv(filename: string, csv: string): void {
  // Prepend a UTF-8 BOM so Excel opens it with the right encoding.
  const blob = new Blob(["﻿" + csv], { type: "text/csv;charset=utf-8" });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
}

// What the Map dialog needs to do its job. Used for both "approve a new mapping"
// (currentCpt=null) and "edit an existing mapping" (currentCpt set).
interface MapDialogTarget {
  serviceCode: string;
  description: string | null;
  year: number | null; // null → suggester defaults to the master's latest year
  currentCpt: string | null;
}

export default function UnmappedCodesPage() {
  const { user } = useAuth();
  const queryClient = useQueryClient();

  const [from, setFrom] = useState(() => localIso(new Date(Date.now() - 60 * 86_400_000)));
  const [to, setTo] = useState(() => localIso(new Date()));
  const [site, setSite] = useState("");
  const [applied, setApplied] = useState<{ from: string; to: string; site: string }>({
    from: localIso(new Date(Date.now() - 60 * 86_400_000)),
    to: localIso(new Date()),
    site: "",
  });
  const [mapTarget, setMapTarget] = useState<MapDialogTarget | null>(null);
  const [showBulk, setShowBulk] = useState(false);
  const [showMappings, setShowMappings] = useState(false);
  const [mappingsFilter, setMappingsFilter] = useState<CrosswalkStatus | "all">("all");

  const report = useQuery({
    queryKey: ["unmapped", applied.from, applied.to, applied.site],
    queryFn: () =>
      billingApi.unmappedCodes({
        from: applied.from,
        to: applied.to,
        site: applied.site || undefined,
      }),
  });

  // Mapping list is window-independent — always loaded so the header counts are
  // truthful even when the management section is collapsed.
  const mappings = useQuery({
    queryKey: ["crosswalk", "list", mappingsFilter],
    queryFn: () =>
      billingApi.listCrosswalk(
        mappingsFilter === "all" ? undefined : { status: mappingsFilter },
      ),
  });

  // Separate query for the header counts so they reflect the totals regardless
  // of the filter chip selected inside the expanded section.
  const mappingsTotals = useQuery({
    queryKey: ["crosswalk", "list", "all-for-counts"],
    queryFn: () => billingApi.listCrosswalk(),
  });

  const statusMutation = useMutation({
    mutationFn: async ({ m, status }: { m: ServiceCodeMapping; status: CrosswalkStatus }) =>
      billingApi.updateCrosswalk(m.serviceCode, {
        serviceCode: m.serviceCode,
        cptCode: m.cptCode,
        status,
        source: m.source,
        note: m.note,
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["unmapped"] });
      queryClient.invalidateQueries({ queryKey: ["crosswalk"] });
    },
  });

  if (user && !canAccessBilling(user.role)) {
    return (
      <div className="mx-auto max-w-3xl px-6 py-10">
        <p className="text-sm text-[color:var(--color-muted-fg)]">
          You don&apos;t have access to billing reports.
        </p>
      </div>
    );
  }

  const data = report.data;
  const codes = data?.codes ?? [];

  const totalsRows = mappingsTotals.data?.rows ?? [];
  const approvedCount = totalsRows.filter((m) => m.status === 1).length;
  const suppressedCount = totalsRows.filter((m) => m.status === 2).length;

  const mappingRows = mappings.data?.rows ?? [];

  return (
    <div className="mx-auto max-w-5xl px-6 py-8 space-y-6">
      <div className="flex items-start justify-between gap-4">
        <div>
          <p className="text-[10px] uppercase tracking-[0.3em] text-[color:var(--color-accent)]">
            Billing
          </p>
          <h1 className="text-3xl mt-1" style={{ fontFamily: "var(--font-display)" }}>
            Unmapped Codes
          </h1>
          <p className="text-sm text-[color:var(--color-muted-fg)] mt-1">
            Service codes on signed reports that earned no RVU credit. Click{" "}
            <span className="font-medium">Map…</span> to suggest a CPT — approved mappings credit
            on the next reconciliation run and drop off this list.
          </p>
          <p className="text-xs text-[color:var(--color-muted-fg)] mt-2">
            <span className="font-medium text-[color:var(--color-base-fg)]">{approvedCount}</span>{" "}
            active mapping{approvedCount === 1 ? "" : "s"} ·{" "}
            <span className="font-medium text-[color:var(--color-base-fg)]">{suppressedCount}</span>{" "}
            suppressed
          </p>
        </div>
        <div className="flex items-center gap-2">
          <Button
            variant="ghost"
            onClick={() => {
              const label = `unmapped-codes_${applied.from}_to_${applied.to}${
                applied.site ? `_${applied.site}` : ""
              }.csv`;
              downloadCsv(label, buildUnmappedCsv(codes));
            }}
            disabled={codes.length === 0}
            title="Download these codes as a crosswalk CSV — fill in cpt_code, then re-import"
          >
            <Download className="size-4" />
            Export CSV
          </Button>
          <Button variant="ghost" onClick={() => setShowBulk(true)}>
            <Upload className="size-4" />
            Bulk import CSV
          </Button>
        </div>
      </div>

      <div className="flex flex-wrap items-end gap-3">
        <div className="space-y-1.5">
          <Label htmlFor="from">From</Label>
          <Input id="from" type="date" value={from} onChange={(e) => setFrom(e.target.value)} className="w-auto" />
        </div>
        <div className="space-y-1.5">
          <Label htmlFor="to">To</Label>
          <Input id="to" type="date" value={to} onChange={(e) => setTo(e.target.value)} className="w-auto" />
        </div>
        <div className="space-y-1.5">
          <Label htmlFor="site">Site (optional)</Label>
          <Input id="site" value={site} onChange={(e) => setSite(e.target.value)} placeholder="site_code" className="w-40" />
        </div>
        <Button onClick={() => setApplied({ from, to, site })} loading={report.isFetching}>
          Generate
        </Button>
      </div>

      {data ? (
        <p className="text-sm text-[color:var(--color-muted-fg)]">
          <span className="font-medium text-[color:var(--color-base-fg)]">{data.totalCodes}</span>{" "}
          unmapped code{data.totalCodes === 1 ? "" : "s"} ·{" "}
          <span className="font-medium text-[color:var(--color-base-fg)]">{data.totalReportsUncredited}</span>{" "}
          report{data.totalReportsUncredited === 1 ? "" : "s"} uncredited
        </p>
      ) : null}

      {report.isError ? (
        <div className="rounded-md border border-[color:var(--color-novarad-red)]/40 bg-[color:var(--color-novarad-red)]/10 px-3 py-3 text-sm text-[color:var(--color-novarad-red)] flex items-center gap-2">
          <AlertCircle className="size-4" />
          Couldn&apos;t load the report.
        </div>
      ) : (
        <div className="rounded-lg border border-[color:var(--color-border)] bg-[color:var(--color-surface)] overflow-hidden">
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead className="text-left text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)] bg-[color:var(--color-surface-2)]">
                <tr>
                  <th className="px-4 py-3 font-medium">Code</th>
                  <th className="px-4 py-3 font-medium">Type</th>
                  <th className="px-4 py-3 font-medium">Description</th>
                  <th className="px-4 py-3 font-medium text-right">Reports</th>
                  <th className="px-4 py-3 font-medium text-right">Lines</th>
                  <th className="px-4 py-3 font-medium"></th>
                </tr>
              </thead>
              <tbody>
                {report.isLoading ? (
                  <tr>
                    <td colSpan={6} className="px-4 py-10 text-center">
                      <Spinner size={20} />
                    </td>
                  </tr>
                ) : codes.length === 0 ? (
                  <tr>
                    <td colSpan={6} className="px-4 py-10 text-center text-sm text-[color:var(--color-muted-fg)]">
                      Nothing unmapped in this window — every signed code matched the master.
                    </td>
                  </tr>
                ) : (
                  codes.map((c) => {
                    const isCpt = c.kind === "cpt_missing_from_master";
                    return (
                      <tr key={`${c.year}-${c.code}`} className="border-t border-[color:var(--color-border)]">
                        <td className="px-4 py-3 font-mono">{c.code}</td>
                        <td className="px-4 py-3">
                          <Badge variant={isCpt ? "accent" : "caution"}>
                            {isCpt ? "Missing CPT" : "Non-CPT"}
                          </Badge>
                        </td>
                        <td className="px-4 py-3 text-[color:var(--color-muted-fg)]">
                          {c.description || "—"}
                        </td>
                        <td className="px-4 py-3 text-right tabular-nums">{c.reportCount}</td>
                        <td className="px-4 py-3 text-right tabular-nums">{c.serviceLineCount}</td>
                        <td className="px-4 py-3 text-right">
                          <Button
                            variant="ghost"
                            size="sm"
                            onClick={() =>
                              setMapTarget({
                                serviceCode: c.code,
                                description: c.description,
                                year: c.year,
                                currentCpt: null,
                              })
                            }
                          >
                            <Wand2 className="size-4" />
                            Map…
                          </Button>
                        </td>
                      </tr>
                    );
                  })
                )}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {/* Mapping management — collapsed by default; open to audit or fix prior decisions. */}
      <div className="rounded-lg border border-[color:var(--color-border)] bg-[color:var(--color-surface)]">
        <button
          type="button"
          onClick={() => setShowMappings((v) => !v)}
          className="w-full flex items-center justify-between px-4 py-3 text-left hover:bg-[color:var(--color-surface-2)] rounded-lg"
        >
          <div className="flex items-center gap-2">
            {showMappings ? (
              <ChevronDown className="size-4 text-[color:var(--color-muted-fg)]" />
            ) : (
              <ChevronRight className="size-4 text-[color:var(--color-muted-fg)]" />
            )}
            <span className="text-sm font-medium">Your mappings</span>
            <span className="text-xs text-[color:var(--color-muted-fg)]">
              · {approvedCount} approved · {suppressedCount} suppressed
            </span>
          </div>
        </button>

        {showMappings ? (
          <div className="border-t border-[color:var(--color-border)] p-4 space-y-3">
            <div className="flex items-center gap-1">
              {(["all", 1, 2] as const).map((s) => (
                <button
                  key={String(s)}
                  type="button"
                  onClick={() => setMappingsFilter(s)}
                  className={`px-2.5 py-1 rounded-md text-xs border ${
                    mappingsFilter === s
                      ? "border-[color:var(--color-accent)] bg-[color:var(--color-accent)]/10 text-[color:var(--color-accent)]"
                      : "border-[color:var(--color-border)] text-[color:var(--color-muted-fg)] hover:bg-[color:var(--color-surface-2)]"
                  }`}
                >
                  {s === "all" ? "All" : s === 1 ? "Approved" : "Suppressed"}
                </button>
              ))}
            </div>

            {mappings.isError ? (
              <div className="rounded-md border border-[color:var(--color-novarad-red)]/40 bg-[color:var(--color-novarad-red)]/10 px-3 py-3 text-sm text-[color:var(--color-novarad-red)] flex items-center gap-2">
                <AlertCircle className="size-4" />
                Couldn&apos;t load mappings.
              </div>
            ) : mappings.isLoading ? (
              <div className="flex justify-center py-6">
                <Spinner size={20} />
              </div>
            ) : mappingRows.length === 0 ? (
              <p className="text-sm text-[color:var(--color-muted-fg)] py-2">
                No mappings yet. Approve one above or use Bulk import CSV.
              </p>
            ) : (
              <div className="overflow-x-auto">
                <table className="w-full text-sm">
                  <thead className="text-left text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)]">
                    <tr>
                      <th className="px-3 py-2 font-medium">Service code</th>
                      <th className="px-3 py-2 font-medium">CPT</th>
                      <th className="px-3 py-2 font-medium">Status</th>
                      <th className="px-3 py-2 font-medium">Source</th>
                      <th className="px-3 py-2 font-medium text-right">Applied</th>
                      <th className="px-3 py-2 font-medium">Last used</th>
                      <th className="px-3 py-2 font-medium">Created by</th>
                      <th className="px-3 py-2 font-medium"></th>
                    </tr>
                  </thead>
                  <tbody>
                    {mappingRows.map((m) => (
                      <tr key={m.crosswalkId} className="border-t border-[color:var(--color-border)]">
                        <td className="px-3 py-2 font-mono">{m.serviceCode}</td>
                        <td className="px-3 py-2 font-mono">{m.cptCode}</td>
                        <td className="px-3 py-2">
                          <Badge variant={m.status === 1 ? "success" : "danger"}>
                            {m.status === 1 ? "Approved" : "Suppressed"}
                          </Badge>
                        </td>
                        <td className="px-3 py-2 text-xs text-[color:var(--color-muted-fg)]">
                          {m.source === 1 ? "manual" : m.source === 2 ? "suggested" : "bulk"}
                        </td>
                        <td className="px-3 py-2 text-right tabular-nums">{m.appliedCount}</td>
                        <td className="px-3 py-2 text-xs text-[color:var(--color-muted-fg)]">
                          {formatRelative(m.lastUsedAt)}
                        </td>
                        <td className="px-3 py-2 text-xs text-[color:var(--color-muted-fg)]">
                          {m.createdByDisplayName ?? "—"}
                        </td>
                        <td className="px-3 py-2">
                          <div className="flex items-center gap-1 justify-end">
                            <Button
                              variant="ghost"
                              size="sm"
                              onClick={() =>
                                setMapTarget({
                                  serviceCode: m.serviceCode,
                                  description: m.approvedForDescription,
                                  year: null,
                                  currentCpt: m.cptCode,
                                })
                              }
                              aria-label="Edit mapping"
                            >
                              <Pencil className="size-4" />
                            </Button>
                            {m.status === 1 ? (
                              <Button
                                variant="ghost"
                                size="sm"
                                loading={statusMutation.isPending}
                                onClick={() => statusMutation.mutate({ m, status: 2 })}
                                aria-label="Suppress"
                              >
                                <Pause className="size-4" />
                              </Button>
                            ) : (
                              <Button
                                variant="ghost"
                                size="sm"
                                loading={statusMutation.isPending}
                                onClick={() => statusMutation.mutate({ m, status: 1 })}
                                aria-label="Re-approve"
                              >
                                <Play className="size-4" />
                              </Button>
                            )}
                          </div>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        ) : null}
      </div>

      <MapCodeDialog
        key={mapTarget?.serviceCode ?? "closed"}
        target={mapTarget}
        onClose={() => setMapTarget(null)}
        onSuccess={() => {
          queryClient.invalidateQueries({ queryKey: ["unmapped"] });
          queryClient.invalidateQueries({ queryKey: ["crosswalk"] });
          setMapTarget(null);
        }}
      />

      <BulkImportDialog open={showBulk} onClose={() => setShowBulk(false)} />
    </div>
  );
}

interface MapCodeDialogProps {
  target: MapDialogTarget | null;
  onClose: () => void;
  onSuccess: () => void;
}

function MapCodeDialog({ target, onClose, onSuccess }: MapCodeDialogProps) {
  const queryClient = useQueryClient();
  const isEdit = target?.currentCpt != null;
  // The parent passes a `key` tied to target.serviceCode, so each open
  // remounts us — these useState initializers seed from the target cleanly.
  const [manualCpt, setManualCpt] = useState(target?.currentCpt ?? "");
  const [note, setNote] = useState("");
  const [picked, setPicked] = useState<CrosswalkSuggestion | null>(null);
  const [formError, setFormError] = useState<string | null>(null);

  // Search field drives the suggester. Pre-filled with the Novarad description
  // when available; the user can clear and type their own search (most unmapped
  // codes don't carry a description, so without this the suggester has nothing
  // to chew on). 250ms debounce so we don't fire a query per keystroke.
  const [searchInput, setSearchInput] = useState(target?.description ?? "");
  const [debouncedSearch, setDebouncedSearch] = useState(searchInput);
  useEffect(() => {
    const id = setTimeout(() => setDebouncedSearch(searchInput), 250);
    return () => clearTimeout(id);
  }, [searchInput]);

  const effectiveDescription = debouncedSearch.trim() || null;

  const suggestions = useQuery({
    queryKey: ["crosswalk", "suggestions", target?.serviceCode, target?.year, effectiveDescription],
    queryFn: () =>
      billingApi.crosswalkSuggestions({
        serviceCode: target!.serviceCode,
        description: effectiveDescription ?? undefined,
        year: target!.year ?? undefined,
        limit: 10,
      }),
    enabled: target !== null,
    staleTime: 60_000,
  });

  function reset() {
    setManualCpt("");
    setNote("");
    setPicked(null);
    setFormError(null);
  }

  const saveMutation = useMutation({
    mutationFn: async () => {
      if (!target) throw new Error("No target");
      const cpt = (picked?.cptCode ?? manualCpt).trim().toUpperCase();
      if (!cpt) throw new Error("Pick a candidate or enter a CPT.");
      const noteSuffix = picked
        ? `accepted suggestion, score ${picked.score.toFixed(2)} (${picked.hitKind})`
        : null;
      const combinedNote = [note.trim(), noteSuffix].filter(Boolean).join(" — ") || null;

      const payload = {
        serviceCode: target.serviceCode,
        cptCode: cpt,
        source: (picked ? 2 : 1) as 1 | 2,
        status: 1 as 1,
        note: combinedNote,
        approvedForDescription: target.description ?? null,
      };

      return isEdit
        ? billingApi.updateCrosswalk(target.serviceCode, payload)
        : billingApi.createCrosswalk(payload);
    },
    onSuccess: () => {
      reset();
      onSuccess();
    },
    onError: (err) => {
      const ax = err as AxiosError<{ error?: string; existing?: { cptCode?: string } }>;
      if (ax?.response?.status === 409) {
        queryClient.invalidateQueries({ queryKey: ["unmapped"] });
        queryClient.invalidateQueries({ queryKey: ["crosswalk"] });
        const existingCpt = ax.response.data?.existing?.cptCode;
        setFormError(
          existingCpt
            ? `Another user just mapped this to ${existingCpt}. Refresh the list.`
            : "Another user just mapped this code. Refresh the list.",
        );
        return;
      }
      setFormError(errMessage(err, "Couldn't save the mapping. Try again."));
    },
  });

  if (!target) return null;
  const sug = suggestions.data;
  const suppressed = sug?.suppressed ?? false;
  const candidates = sug?.candidates ?? [];
  const existing = sug?.existing ?? null;
  const canSave = !suppressed && Boolean(picked?.cptCode || manualCpt.trim());
  const title = isEdit
    ? `Edit mapping for ${target.serviceCode}`
    : `Map ${target.serviceCode} to a CPT`;

  return (
    <Dialog
      open={target !== null}
      onClose={() => {
        reset();
        onClose();
      }}
      title={title}
      description={target.description ?? undefined}
    >
      <div className="space-y-4">
        {suppressed ? (
          <div className="rounded-md border border-[color:var(--color-novarad-red)]/40 bg-[color:var(--color-novarad-red)]/10 px-3 py-3 text-sm">
            This code is currently <strong>suppressed</strong>. To credit it again, re-approve from
            the mappings list below.
          </div>
        ) : null}

        {existing && !suppressed && !isEdit ? (
          <div className="rounded-md border border-[color:var(--color-accent)]/40 bg-[color:var(--color-accent)]/10 px-3 py-2 text-xs">
            Already mapped to <span className="font-mono font-medium">{existing.cptCode}</span>.
            Saving will overwrite (audited).
          </div>
        ) : null}

        {isEdit && target.currentCpt ? (
          <div className="rounded-md border border-[color:var(--color-border)] bg-[color:var(--color-surface-2)] px-3 py-2 text-xs">
            Currently mapped to <span className="font-mono font-medium">{target.currentCpt}</span>.
          </div>
        ) : null}

        <div className="space-y-1.5">
          <Label htmlFor="search">Search the CPT master</Label>
          <div className="relative">
            <Search className="size-4 absolute left-3 top-1/2 -translate-y-1/2 text-[color:var(--color-muted-fg)] pointer-events-none" />
            <Input
              id="search"
              value={searchInput}
              onChange={(e) => setSearchInput(e.target.value)}
              placeholder="e.g. chest x-ray, CT abdomen, mammogram"
              className="pl-9"
              disabled={suppressed}
            />
          </div>
          <p className="text-[11px] text-[color:var(--color-muted-fg)]">
            Type any keywords from the procedure description — results below update as you type.
          </p>
        </div>

        <Card>
          <CardHeader>
            <CardTitle className="text-sm">Suggested CPTs</CardTitle>
          </CardHeader>
          <CardBody className="space-y-1.5 max-h-72 overflow-y-auto">
            {suggestions.isLoading ? (
              <div className="flex justify-center py-6">
                <Spinner size={20} />
              </div>
            ) : candidates.length === 0 ? (
              <p className="text-xs text-[color:var(--color-muted-fg)]">
                {suppressed
                  ? "—"
                  : effectiveDescription
                    ? "No matches yet — try different keywords, or enter a CPT manually below."
                    : "Type a search above to look up CPTs in the master, or enter a CPT manually below."}
              </p>
            ) : (
              candidates.map((c) => {
                const selected = picked?.cptCode === c.cptCode;
                return (
                  <button
                    key={`${c.cptCode}-${c.hitKind}`}
                    type="button"
                    onClick={() => {
                      setPicked(c);
                      setManualCpt("");
                    }}
                    className={`w-full text-left rounded-md border px-3 py-2 transition ${
                      selected
                        ? "border-[color:var(--color-accent)] bg-[color:var(--color-accent)]/10"
                        : "border-[color:var(--color-border)] hover:bg-[color:var(--color-surface-2)]"
                    }`}
                  >
                    <div className="flex items-center justify-between gap-3">
                      <div className="min-w-0">
                        <div className="font-mono text-sm">{c.cptCode}</div>
                        <div className="text-xs text-[color:var(--color-muted-fg)] truncate">{c.description}</div>
                      </div>
                      <div className="shrink-0 text-right text-xs">
                        <div className="tabular-nums">RVU {Number(c.workRvu).toFixed(2)}</div>
                        <Badge variant={c.hitKind === "exact_code" ? "accent" : "neutral"}>
                          {c.hitKind === "exact_code" ? "exact" : c.score.toFixed(2)}
                        </Badge>
                      </div>
                    </div>
                  </button>
                );
              })
            )}
          </CardBody>
        </Card>

        <div className="space-y-1.5">
          <Label htmlFor="manualCpt">{isEdit ? "CPT code" : "Or enter a CPT manually"}</Label>
          <Input
            id="manualCpt"
            value={manualCpt}
            onChange={(e) => {
              setManualCpt(e.target.value);
              setPicked(null);
            }}
            placeholder="e.g. 71046"
            maxLength={20}
            disabled={suppressed}
          />
        </div>

        <div className="space-y-1.5">
          <Label htmlFor="note">Note (optional)</Label>
          <Textarea
            id="note"
            value={note}
            onChange={(e) => setNote(e.target.value)}
            placeholder="Why this mapping — any context to capture for later."
            rows={2}
            maxLength={500}
            disabled={suppressed}
          />
        </div>

        {formError ? (
          <p className="text-sm text-[color:var(--color-novarad-red)]">{formError}</p>
        ) : null}
      </div>

      <DialogActions>
        <Button
          variant="ghost"
          onClick={() => {
            reset();
            onClose();
          }}
        >
          Cancel
        </Button>
        <Button
          onClick={() => saveMutation.mutate()}
          loading={saveMutation.isPending}
          disabled={!canSave}
        >
          {isEdit ? "Save changes" : "Approve mapping"}
        </Button>
      </DialogActions>
    </Dialog>
  );
}

// ----------------------------------------------------------------------------
// CSV bulk import — minimal RFC-4180-ish parser. The expected file is small
// (a few hundred rows of the top-power-law mnemonics), so a streaming parser
// is overkill. Handles quoted fields, embedded quotes ("" → "), and CRLF/LF.
// ----------------------------------------------------------------------------

function parseCsv(text: string): string[][] {
  const rows: string[][] = [];
  let i = 0;
  let field = "";
  let row: string[] = [];
  let inQuotes = false;
  while (i < text.length) {
    const ch = text[i];
    if (inQuotes) {
      if (ch === '"') {
        if (text[i + 1] === '"') {
          field += '"';
          i += 2;
          continue;
        }
        inQuotes = false;
        i++;
        continue;
      }
      field += ch;
      i++;
      continue;
    }
    if (ch === '"') {
      inQuotes = true;
      i++;
      continue;
    }
    if (ch === ",") {
      row.push(field);
      field = "";
      i++;
      continue;
    }
    if (ch === "\r") {
      i++;
      continue;
    }
    if (ch === "\n") {
      row.push(field);
      rows.push(row);
      field = "";
      row = [];
      i++;
      continue;
    }
    field += ch;
    i++;
  }
  if (field.length > 0 || row.length > 0) {
    row.push(field);
    rows.push(row);
  }
  return rows.filter((r) => r.some((c) => c.trim().length > 0));
}

interface BulkImportDialogProps {
  open: boolean;
  onClose: () => void;
}

function BulkImportDialog({ open, onClose }: BulkImportDialogProps) {
  const queryClient = useQueryClient();
  const [rows, setRows] = useState<BulkImportRow[] | null>(null);
  const [fileName, setFileName] = useState<string>("");
  const [parseError, setParseError] = useState<string | null>(null);
  const [onConflict, setOnConflict] = useState<"skip" | "update">("skip");
  const [result, setResult] = useState<BulkImportResult | null>(null);
  const [formError, setFormError] = useState<string | null>(null);

  function reset() {
    setRows(null);
    setFileName("");
    setParseError(null);
    setOnConflict("skip");
    setResult(null);
    setFormError(null);
  }

  async function handleFile(file: File) {
    setParseError(null);
    setResult(null);
    setFileName(file.name);
    const text = await file.text();
    const grid = parseCsv(text);
    if (grid.length === 0) {
      setRows(null);
      setParseError("CSV is empty.");
      return;
    }
    const header = grid[0].map((h) => h.trim().toLowerCase());
    const scCol = header.findIndex((h) => h === "service_code" || h === "servicecode");
    const cptCol = header.findIndex((h) => h === "cpt_code" || h === "cptcode" || h === "cpt");
    const noteCol = header.findIndex((h) => h === "note");
    if (scCol < 0 || cptCol < 0) {
      setRows(null);
      setParseError("CSV must have a header with service_code and cpt_code columns.");
      return;
    }
    const out: BulkImportRow[] = [];
    for (let i = 1; i < grid.length; i++) {
      const r = grid[i];
      const sc = (r[scCol] ?? "").trim();
      const cpt = (r[cptCol] ?? "").trim();
      const note = noteCol >= 0 ? (r[noteCol] ?? "").trim() : "";
      if (!sc || !cpt) continue;
      out.push({ serviceCode: sc, cptCode: cpt, note: note || null });
    }
    if (out.length === 0) {
      setRows(null);
      setParseError("No data rows after the header.");
      return;
    }
    setRows(out);
  }

  const importMutation = useMutation({
    mutationFn: async () => {
      if (!rows) throw new Error("Pick a CSV first.");
      return billingApi.bulkImportCrosswalk({ rows, onConflict });
    },
    onSuccess: (res) => {
      setResult(res);
      queryClient.invalidateQueries({ queryKey: ["crosswalk"] });
      queryClient.invalidateQueries({ queryKey: ["unmapped"] });
    },
    onError: (err) => setFormError(errMessage(err, "Bulk import failed.")),
  });

  return (
    <Dialog
      open={open}
      onClose={() => {
        reset();
        onClose();
      }}
      title="Bulk import crosswalk CSV"
      description="Header: service_code,cpt_code,note (note optional). Tip: use Export CSV, fill in cpt_code, and re-import here — extra columns are ignored and rows with a blank cpt_code are skipped."
    >
      <div className="space-y-4">
        <div className="space-y-1.5">
          <Label htmlFor="csv">CSV file</Label>
          <Input
            id="csv"
            type="file"
            accept=".csv,text/csv"
            onChange={(e) => {
              const f = e.target.files?.[0];
              if (f) void handleFile(f);
            }}
          />
          {fileName ? (
            <p className="text-xs text-[color:var(--color-muted-fg)]">
              {fileName} · {rows?.length ?? 0} row(s) parsed
            </p>
          ) : null}
          {parseError ? (
            <p className="text-sm text-[color:var(--color-novarad-red)]">{parseError}</p>
          ) : null}
        </div>

        <div className="space-y-1.5">
          <Label htmlFor="onConflict">If a service_code already exists</Label>
          <div className="flex items-center gap-2">
            {(["skip", "update"] as const).map((opt) => (
              <button
                key={opt}
                type="button"
                onClick={() => setOnConflict(opt)}
                className={`px-2.5 py-1 rounded-md text-xs border ${
                  onConflict === opt
                    ? "border-[color:var(--color-accent)] bg-[color:var(--color-accent)]/10 text-[color:var(--color-accent)]"
                    : "border-[color:var(--color-border)] text-[color:var(--color-muted-fg)] hover:bg-[color:var(--color-surface-2)]"
                }`}
              >
                {opt === "skip" ? "Skip" : "Update existing"}
              </button>
            ))}
          </div>
        </div>

        {result ? (
          <div className="rounded-md border border-[color:var(--color-border)] bg-[color:var(--color-surface-2)] px-3 py-3 text-sm space-y-1.5">
            <div>
              <span className="font-medium">{result.inserted}</span> inserted ·{" "}
              <span className="font-medium">{result.updated}</span> updated ·{" "}
              <span className="font-medium">{result.skipped}</span> skipped ·{" "}
              <span className="font-medium">{result.errors}</span> error(s)
            </div>
            {result.errors > 0 ? (
              <ul className="text-xs text-[color:var(--color-novarad-red)] space-y-0.5">
                {result.rows
                  .filter((r) => r.outcome === "error")
                  .slice(0, 10)
                  .map((r) => (
                    <li key={r.serviceCode}>
                      {r.serviceCode}: {r.error}
                    </li>
                  ))}
              </ul>
            ) : null}
          </div>
        ) : null}

        {formError ? (
          <p className="text-sm text-[color:var(--color-novarad-red)]">{formError}</p>
        ) : null}
      </div>

      <DialogActions>
        <Button
          variant="ghost"
          onClick={() => {
            reset();
            onClose();
          }}
        >
          Close
        </Button>
        <Button
          onClick={() => importMutation.mutate()}
          loading={importMutation.isPending}
          disabled={!rows || rows.length === 0}
        >
          Import {rows ? `${rows.length} row${rows.length === 1 ? "" : "s"}` : ""}
        </Button>
      </DialogActions>
    </Dialog>
  );
}

