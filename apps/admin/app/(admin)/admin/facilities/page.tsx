"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AxiosError } from "axios";
import { Building2, DownloadCloud, Pencil, Plus, Trash2, X } from "lucide-react";
import { useState } from "react";

import { Field, inputCls } from "@/components/notifications/notification-bits";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Spinner } from "@/components/ui/spinner";
import { facilitiesApi } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import type { FacilityAdmin } from "@/lib/types";

interface FacilityFormState {
  novaradFacilityId: string;
  code: string;
  displayName: string;
  isActive: boolean;
}

export default function FacilitiesPage() {
  const qc = useQueryClient();
  const { user: me, isHydrated } = useAuth();
  const [search, setSearch] = useState("");
  const [activeOnly, setActiveOnly] = useState(false);
  const [editing, setEditing] = useState<"new" | number | null>(null);
  const [confirmDeleteId, setConfirmDeleteId] = useState<number | null>(null);
  const [note, setNote] = useState<string | null>(null);

  const facilities = useQuery({
    queryKey: ["admin-facilities"],
    queryFn: () => facilitiesApi.list(),
    enabled: !!me,
  });
  const invalidate = () => qc.invalidateQueries({ queryKey: ["admin-facilities"] });

  const importMut = useMutation({
    mutationFn: () => facilitiesApi.importFromNovarad(),
    onSuccess: (r) => {
      setNote(`Imported from Novarad: ${r.inserted} new, ${r.updated} updated (${r.total} read).`);
      void invalidate();
    },
    onError: (err) => setNote(errText(err, "Import failed.")),
  });
  const toggleMut = useMutation({
    mutationFn: (f: FacilityAdmin) =>
      facilitiesApi.update(f.facilityId, {
        novaradFacilityId: f.novaradFacilityId,
        code: f.code,
        displayName: f.displayName,
        isActive: !f.isActive,
      }),
    onSuccess: invalidate,
    onError: (err) => setNote(errText(err, "Couldn't update the facility.")),
  });
  const deleteMut = useMutation({
    mutationFn: (id: number) => facilitiesApi.remove(id),
    onSuccess: () => {
      setConfirmDeleteId(null);
      void invalidate();
    },
    onError: (err) => {
      setNote(errText(err, "Couldn't delete the facility."));
      setConfirmDeleteId(null);
    },
  });

  if (!isHydrated || !me) {
    return (
      <div className="min-h-[60vh] flex items-center justify-center">
        <Spinner size={28} />
      </div>
    );
  }

  const q = search.trim().toLowerCase();
  const rows = (facilities.data ?? []).filter(
    (f) =>
      (!activeOnly || f.isActive) &&
      (!q ||
        f.code.toLowerCase().includes(q) ||
        f.displayName.toLowerCase().includes(q) ||
        String(f.novaradFacilityId).includes(q)),
  );

  return (
    <div className="mx-auto max-w-6xl px-6 py-8">
      <div className="mb-6 flex flex-wrap items-end justify-between gap-4 rise-in">
        <div>
          <p className="text-[10px] uppercase tracking-[0.3em] text-[color:var(--color-accent)]">
            Admin
          </p>
          <h1 className="text-3xl mt-1" style={{ fontFamily: "var(--font-display)" }}>
            Facilities<span className="caret-blink text-[color:var(--color-accent)]">▍</span>
          </h1>
          <p className="text-sm text-[color:var(--color-muted-fg)] mt-1 max-w-2xl">
            The tenant&apos;s Novarad facilities, mapped into Radiology Plus — user access and
            federated sign-ins resolve through this list.
          </p>
        </div>
        <div className="flex items-center gap-2">
          <Button
            variant="secondary"
            size="sm"
            loading={importMut.isPending}
            onClick={() => importMut.mutate()}
            title="Pull the facility list from the live Novarad DB"
          >
            <DownloadCloud className="size-4" />
            Import from Novarad
          </Button>
          <Button size="sm" onClick={() => setEditing(editing === "new" ? null : "new")}>
            <Plus className="size-4" />
            New facility
          </Button>
        </div>
      </div>

      {editing === "new" ? (
        <FacilityForm
          initial={{ novaradFacilityId: "", code: "", displayName: "", isActive: true }}
          onDone={(msg) => {
            setEditing(null);
            if (msg) setNote(msg);
            void invalidate();
          }}
        />
      ) : null}

      {note ? <p className="mb-4 text-sm text-[color:var(--color-accent)]">{note}</p> : null}

      <div className="mb-4 flex flex-wrap items-center gap-3">
        <input
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          placeholder="Search code, name, or Novarad id…"
          aria-label="Search facilities"
          className={`${inputCls} w-72`}
        />
        <label className="inline-flex items-center gap-2 text-sm cursor-pointer select-none">
          <input
            type="checkbox"
            checked={activeOnly}
            onChange={(e) => setActiveOnly(e.target.checked)}
            className="size-4 accent-[color:var(--color-accent)]"
          />
          Active only
        </label>
        <span className="text-xs text-[color:var(--color-muted-fg)]">
          {rows.length} of {(facilities.data ?? []).length}
        </span>
      </div>

      {facilities.isLoading ? (
        <div className="min-h-[30vh] flex items-center justify-center">
          <Spinner size={24} />
        </div>
      ) : facilities.isError ? (
        <p className="text-sm text-[color:var(--color-novarad-red)]">
          Couldn&apos;t load facilities.{" "}
          <button className="underline underline-offset-2" onClick={() => facilities.refetch()}>
            Try again
          </button>
        </p>
      ) : rows.length === 0 ? (
        <div className="rounded-lg border border-dashed border-[color:var(--color-border)] px-6 py-16 text-center rise-in">
          <Building2 className="size-8 mx-auto text-[color:var(--color-accent)]" />
          <p className="mt-3 text-sm text-[color:var(--color-muted-fg)]">
            {q || activeOnly
              ? "Nothing matches these filters."
              : "No facilities yet — import them from Novarad, or add one by hand."}
          </p>
        </div>
      ) : (
        <ul className="space-y-1.5">
          {rows.map((f) => (
            <li key={f.facilityId}>
              <div className="flex flex-wrap items-center gap-x-3 gap-y-1.5 rounded-lg border border-[color:var(--color-border)] bg-[color:var(--color-surface)] px-4 py-2 transition-[border-color] hover:border-[color:var(--color-accent)]/40">
                <span className="w-14 font-mono text-xs text-[color:var(--color-muted-fg)]">
                  #{f.novaradFacilityId}
                </span>
                <span className="font-medium" style={{ fontFamily: "var(--font-display)" }}>
                  {f.code}
                </span>
                <span className="min-w-0 flex-1 truncate text-sm text-[color:var(--color-muted-fg)]">
                  {f.displayName}
                </span>
                {f.userCount > 0 ? (
                  <Badge variant="neutral">
                    {f.userCount} user{f.userCount === 1 ? "" : "s"}
                  </Badge>
                ) : null}
                {f.isActive ? null : <Badge variant="neutral">inactive</Badge>}

                <div className="flex items-center gap-1.5">
                  {confirmDeleteId === f.facilityId ? (
                    <>
                      <span className="text-xs text-[color:var(--color-caution)]">Delete?</span>
                      <Button
                        variant="danger"
                        size="sm"
                        loading={deleteMut.isPending}
                        onClick={() => deleteMut.mutate(f.facilityId)}
                      >
                        Confirm
                      </Button>
                      <Button variant="ghost" size="sm" onClick={() => setConfirmDeleteId(null)}>
                        Cancel
                      </Button>
                    </>
                  ) : (
                    <>
                      <Button
                        variant="ghost"
                        size="sm"
                        loading={toggleMut.isPending && toggleMut.variables?.facilityId === f.facilityId}
                        onClick={() => toggleMut.mutate(f)}
                      >
                        {f.isActive ? "Deactivate" : "Activate"}
                      </Button>
                      <button
                        type="button"
                        onClick={() => setEditing(editing === f.facilityId ? null : f.facilityId)}
                        title="Edit"
                        aria-label={`Edit ${f.code}`}
                        className="inline-flex items-center rounded-md p-1.5 text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-accent)] hover:bg-[color:var(--color-surface-2)] transition-colors"
                      >
                        <Pencil className="size-3.5" />
                      </button>
                      <button
                        type="button"
                        onClick={() => setConfirmDeleteId(f.facilityId)}
                        title="Delete"
                        aria-label={`Delete ${f.code}`}
                        className="inline-flex items-center rounded-md p-1.5 text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-novarad-red)] hover:bg-[color:var(--color-surface-2)] transition-colors"
                      >
                        <Trash2 className="size-3.5" />
                      </button>
                    </>
                  )}
                </div>
              </div>
              {editing === f.facilityId ? (
                <FacilityForm
                  facilityId={f.facilityId}
                  initial={{
                    novaradFacilityId: String(f.novaradFacilityId),
                    code: f.code,
                    displayName: f.displayName,
                    isActive: f.isActive,
                  }}
                  onDone={(msg) => {
                    setEditing(null);
                    if (msg) setNote(msg);
                    void invalidate();
                  }}
                />
              ) : null}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function errText(err: unknown, fallback: string): string {
  const ax = err as AxiosError<{ error?: string }>;
  return ax.response?.data?.error ?? fallback;
}

function FacilityForm({
  facilityId,
  initial,
  onDone,
}: {
  facilityId?: number; // undefined = create mode
  initial: FacilityFormState;
  onDone: (message: string | null) => void;
}) {
  const [form, setForm] = useState<FacilityFormState>(initial);
  const [error, setError] = useState<string | null>(null);
  const isEdit = facilityId !== undefined;

  const save = useMutation({
    mutationFn: () => {
      const body = {
        novaradFacilityId: Number(form.novaradFacilityId),
        code: form.code.trim(),
        displayName: form.displayName.trim(),
        isActive: form.isActive,
      };
      return isEdit ? facilitiesApi.update(facilityId, body) : facilitiesApi.create(body);
    },
    onSuccess: (f) => onDone(`${f.code} ${isEdit ? "saved" : "created"}.`),
    onError: (err) => setError(errText(err, "Couldn't save the facility.")),
  });

  function submit() {
    if (!/^\d+$/.test(form.novaradFacilityId.trim())) {
      setError("Novarad facility ID must be a number.");
      return;
    }
    if (!form.code.trim() || !form.displayName.trim()) {
      setError("Code and display name are required.");
      return;
    }
    setError(null);
    save.mutate();
  }

  return (
    <div className="mb-2 mt-1 space-y-4 rounded-lg border border-[color:var(--color-accent)]/40 bg-[color:var(--color-surface)] px-4 py-4 rise-in">
      <div className="flex items-center justify-between">
        <h2 className="text-sm font-medium" style={{ fontFamily: "var(--font-display)" }}>
          {isEdit ? `Edit ${initial.code}` : "New facility"}
        </h2>
        <button
          type="button"
          onClick={() => onDone(null)}
          aria-label="Close form"
          className="rounded p-1 text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-base-fg)]"
        >
          <X className="size-4" />
        </button>
      </div>

      <div className="grid gap-4 sm:grid-cols-3">
        <Field label="Novarad facility ID" required>
          <input
            value={form.novaradFacilityId}
            onChange={(e) => setForm((f) => ({ ...f, novaradFacilityId: e.target.value }))}
            inputMode="numeric"
            className={`${inputCls} font-mono`}
          />
        </Field>
        <Field label="Code" required>
          <input
            value={form.code}
            onChange={(e) => setForm((f) => ({ ...f, code: e.target.value }))}
            className={inputCls}
          />
        </Field>
        <Field label="Display name" required>
          <input
            value={form.displayName}
            onChange={(e) => setForm((f) => ({ ...f, displayName: e.target.value }))}
            className={inputCls}
          />
        </Field>
      </div>
      <label className="inline-flex items-center gap-2 text-sm cursor-pointer select-none">
        <input
          type="checkbox"
          checked={form.isActive}
          onChange={(e) => setForm((f) => ({ ...f, isActive: e.target.checked }))}
          className="size-4 accent-[color:var(--color-accent)]"
        />
        Active
      </label>

      {error ? <p className="text-sm text-[color:var(--color-novarad-red)]">{error}</p> : null}

      <div className="flex items-center gap-2">
        <Button size="sm" loading={save.isPending} onClick={submit}>
          {isEdit ? "Save changes" : "Create facility"}
        </Button>
        <Button variant="ghost" size="sm" onClick={() => onDone(null)}>
          Cancel
        </Button>
      </div>
    </div>
  );
}
