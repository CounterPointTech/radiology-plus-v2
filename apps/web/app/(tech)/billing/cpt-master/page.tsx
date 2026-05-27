"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AlertCircle, Check, Search, X } from "lucide-react";
import { useRouter } from "next/navigation";
import { useEffect, useMemo, useRef, useState } from "react";

import { ImportUploader } from "@/components/billing/import-uploader";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Spinner } from "@/components/ui/spinner";
import { billingApi } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { canAccessBilling, type CptCode } from "@/lib/types";
import { cn, formatDateTime } from "@/lib/utils";

const PAGE_SIZE = 100;

function useDebounced<T>(value: T, ms = 250): T {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const t = setTimeout(() => setDebounced(value), ms);
    return () => clearTimeout(t);
  }, [value, ms]);
  return debounced;
}

export default function CptMasterPage() {
  const router = useRouter();
  const queryClient = useQueryClient();
  const { user, isHydrated } = useAuth();

  const currentYear = new Date().getFullYear();
  const [year, setYear] = useState(currentYear);
  const [search, setSearch] = useState("");
  const debouncedSearch = useDebounced(search);

  const patchMutation = useMutation({
    mutationFn: (input: {
      code: string;
      workRvu?: number;
      description?: string;
    }) =>
      billingApi.patchCptCode(input.code, {
        year,
        workRvu: input.workRvu,
        description: input.description,
      }),
    onSuccess: (updated) => {
      // Patch the row in the cache without refetching the whole page.
      queryClient.setQueryData<CptCode[]>(
        ["cpt-master", year, debouncedSearch],
        (prev) => prev?.map((c) => (c.code === updated.code ? updated : c)),
      );
    },
  });

  // Role gate — Tech-validation layout only gates on auth. Billing is
  // NRS+Admin only, enforced here in addition to the server-side check.
  useEffect(() => {
    if (isHydrated && user && !canAccessBilling(user.role)) {
      router.replace("/validation");
    }
  }, [isHydrated, user, router]);

  const codes = useQuery({
    queryKey: ["cpt-master", year, debouncedSearch],
    queryFn: () =>
      billingApi.listCptMaster({
        year,
        q: debouncedSearch || undefined,
        limit: PAGE_SIZE,
      }),
    enabled: !!user && canAccessBilling(user.role),
  });

  const imports = useQuery({
    queryKey: ["cpt-imports"],
    queryFn: () => billingApi.listImports(5),
    enabled: !!user && canAccessBilling(user.role),
  });

  const rows = codes.data ?? [];
  const lastImport = imports.data?.[0] ?? null;

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
          <h1
            className="text-3xl mt-1"
            style={{ fontFamily: "var(--font-display)" }}
          >
            CPT master
          </h1>
          <p className="text-sm text-[color:var(--color-muted-fg)] mt-1">
            Per-year CPT and work-RVU table. Loaded from the annual RVU spreadsheet.
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
          <Button variant="secondary" size="sm" onClick={() => codes.refetch()} disabled={codes.isFetching}>
            Refresh
          </Button>
        </div>
      </div>

      <ImportUploader
        year={year}
        onImported={() => {
          queryClient.invalidateQueries({ queryKey: ["cpt-master"] });
          queryClient.invalidateQueries({ queryKey: ["cpt-imports"] });
        }}
      />

      {lastImport ? (
        <div className="mb-4 rounded-md border border-[color:var(--color-border)] bg-[color:var(--color-surface)] px-4 py-3 text-xs flex flex-wrap items-center gap-x-4 gap-y-1">
          <span className="text-[color:var(--color-muted-fg)]">Last import:</span>
          <span className="font-mono">{lastImport.fileName}</span>
          <span className="text-[color:var(--color-muted-fg)]">·</span>
          <span>
            <strong>{lastImport.parsedRows}</strong> parsed (
            <span className="text-[color:var(--color-accent)]">{lastImport.insertedRows} inserted</span>,{" "}
            <span className="text-[color:var(--color-muted-fg)]">{lastImport.updatedRows} updated</span>
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
            placeholder="Search by CPT code or description"
            aria-label="Search CPT master"
            className="pl-9"
          />
        </div>
        {codes.isFetching ? <Spinner size={16} /> : null}
        <span className="ml-auto text-xs text-[color:var(--color-muted-fg)]">
          {codes.data ? `${rows.length} shown${rows.length === PAGE_SIZE ? "+" : ""}` : null}
        </span>
      </div>

      {codes.isError ? (
        <div className="mb-3 rounded-md border border-[color:var(--color-novarad-red)]/40 bg-[color:var(--color-novarad-red)]/10 px-3 py-3 text-sm text-[color:var(--color-novarad-red)] flex items-center gap-2">
          <AlertCircle className="size-4" />
          Couldn&apos;t load the CPT master.
          <button className="underline underline-offset-2" onClick={() => codes.refetch()}>
            Try again
          </button>
        </div>
      ) : null}

      <div className="rounded-lg border border-[color:var(--color-border)] bg-[color:var(--color-surface)] overflow-hidden">
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead className="text-left text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)] bg-[color:var(--color-surface-2)]">
              <tr>
                <th className="px-4 py-3 font-medium w-44">CPT</th>
                <th className="px-4 py-3 font-medium">Description</th>
                <th className="px-4 py-3 font-medium text-right w-28">Work RVU</th>
                <th className="px-4 py-3 font-medium w-32">Updated</th>
              </tr>
            </thead>
            <tbody>
              {codes.isLoading ? (
                <tr>
                  <td colSpan={4} className="px-4 py-10 text-center text-[color:var(--color-muted-fg)]">
                    <Spinner size={20} />
                  </td>
                </tr>
              ) : rows.length === 0 && !codes.isError ? (
                <tr>
                  <td
                    colSpan={4}
                    className="px-4 py-10 text-center text-sm text-[color:var(--color-muted-fg)]"
                  >
                    {debouncedSearch
                      ? `No CPT codes matching "${debouncedSearch}" in ${year}.`
                      : `No CPT codes for ${year}. Run an import to load the annual master.`}
                  </td>
                </tr>
              ) : (
                rows.map((c) => (
                  <CptRow
                    key={c.code}
                    code={c}
                    saving={
                      patchMutation.isPending &&
                      patchMutation.variables?.code === c.code
                    }
                    onSave={(patch) =>
                      patchMutation.mutate({ code: c.code, ...patch })
                    }
                  />
                ))
              )}
            </tbody>
          </table>
        </div>
      </div>

      {rows.length === PAGE_SIZE ? (
        <p className="mt-3 text-xs text-[color:var(--color-muted-fg)]">
          Showing the first {PAGE_SIZE} rows. Refine your search to narrow the list.
        </p>
      ) : null}

      {patchMutation.isError ? (
        <p className="mt-3 text-sm text-[color:var(--color-novarad-red)]">
          Couldn&apos;t save that edit. Try again, or refresh the page if it keeps failing.
        </p>
      ) : null}
    </div>
  );
}

interface CptRowProps {
  code: CptCode;
  saving: boolean;
  onSave: (patch: { workRvu?: number; description?: string }) => void;
}

function CptRow({ code, saving, onSave }: CptRowProps) {
  return (
    <tr className="border-t border-[color:var(--color-border)] hover:bg-[color:var(--color-surface-2)]/50">
      <td className="px-4 py-2.5 font-mono text-xs">
        {code.code.includes(";") ? (
          <span className="inline-flex items-center gap-2">
            <span>{code.code}</span>
            <Badge variant="caution" title="Multi-code bundle">
              bundle
            </Badge>
          </span>
        ) : (
          code.code
        )}
      </td>
      <td className="px-4 py-2.5">
        <EditableCell
          value={code.description}
          type="text"
          ariaLabel={`Description for ${code.code}`}
          saving={saving}
          onSave={(v) =>
            v !== code.description ? onSave({ description: v }) : undefined
          }
        />
      </td>
      <td className="px-4 py-2.5 font-mono text-right tabular-nums">
        <EditableCell
          value={code.workRvu.toFixed(2)}
          type="number"
          ariaLabel={`Work RVU for ${code.code}`}
          align="right"
          saving={saving}
          onSave={(v) => {
            const parsed = Number(v);
            if (!Number.isFinite(parsed) || parsed < 0) return;
            if (parsed !== code.workRvu) onSave({ workRvu: parsed });
          }}
        />
      </td>
      <td className="px-4 py-2.5 text-xs text-[color:var(--color-muted-fg)]">
        {formatDateTime(code.updatedAt)}
      </td>
    </tr>
  );
}

interface EditableCellProps {
  value: string;
  type: "text" | "number";
  ariaLabel: string;
  saving: boolean;
  align?: "left" | "right";
  onSave: (next: string) => void;
}

function EditableCell({
  value,
  type,
  ariaLabel,
  saving,
  align = "left",
  onSave,
}: EditableCellProps) {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(value);
  const inputRef = useRef<HTMLInputElement | null>(null);

  // Reset the draft when the source value changes underneath us
  // (e.g. another tab edited the row).
  useEffect(() => {
    if (!editing) setDraft(value);
  }, [value, editing]);

  useEffect(() => {
    if (editing) inputRef.current?.focus();
  }, [editing]);

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
        className={cn(
          "block w-full -mx-1 px-1 py-0.5 rounded text-left hover:bg-[color:var(--color-surface-2)] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-accent)]/50",
          align === "right" && "text-right",
        )}
        aria-label={`Edit ${ariaLabel}`}
        disabled={saving}
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
        // onMouseDown so the input doesn't fire blur before our click handler.
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
