"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AlertCircle, Check, Pencil, X } from "lucide-react";
import { useState } from "react";

import { BannerView } from "@/components/status-banner";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Spinner } from "@/components/ui/spinner";
import { adminApi } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import {
  BannerSeverity,
  canAccessAdmin,
  type StatusBanner,
} from "@/lib/types";
import { formatDateTime } from "@/lib/utils";

const SEVERITY_OPTIONS: { value: number; label: string }[] = [
  { value: BannerSeverity.Info, label: "Info" },
  { value: BannerSeverity.Maintenance, label: "Maintenance" },
  { value: BannerSeverity.Warning, label: "Warning" },
  { value: BannerSeverity.Critical, label: "Critical" },
];

const SEVERITY_LABEL: Record<number, string> = Object.fromEntries(
  SEVERITY_OPTIONS.map((o) => [o.value, o.label]),
);

// datetime-local <-> ISO. The input is naive local wall-clock; Date() reads it as local
// and toISOString() converts to the instant the API + DB compare against NOW().
function localInputToIso(local: string): string | null {
  if (!local) return null;
  const d = new Date(local);
  return Number.isNaN(d.getTime()) ? null : d.toISOString();
}
function isoToLocalInput(iso: string | null): string {
  if (!iso) return "";
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "";
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

const EMPTY = {
  message: "",
  severity: BannerSeverity.Info as number,
  isAnimated: true,
  marqueeSpeed: 5,
  isActive: true,
  startsLocal: "",
  endsLocal: "",
};

export default function StatusBannerAdminPage() {
  const { user, isHydrated } = useAuth();
  const queryClient = useQueryClient();

  const [editingId, setEditingId] = useState<number | null>(null);
  const [form, setForm] = useState({ ...EMPTY });
  const [formError, setFormError] = useState<string | null>(null);
  const [confirmDeleteId, setConfirmDeleteId] = useState<number | null>(null);

  const banners = useQuery({
    queryKey: ["status-banners"],
    queryFn: () => adminApi.listBanners(),
    enabled: !!user && canAccessAdmin(user.role),
  });

  function invalidateAll() {
    queryClient.invalidateQueries({ queryKey: ["status-banners"] });
    queryClient.invalidateQueries({ queryKey: ["active-banner"] });
  }

  function resetForm() {
    setEditingId(null);
    setForm({ ...EMPTY });
    setFormError(null);
  }

  const save = useMutation({
    mutationFn: async () => {
      const startsAt = localInputToIso(form.startsLocal);
      const endsAt = localInputToIso(form.endsLocal);
      if (editingId == null) {
        return adminApi.createBanner({
          message: form.message.trim(),
          severity: form.severity,
          isAnimated: form.isAnimated,
          isActive: form.isActive,
          marqueeSpeed: form.marqueeSpeed,
          startsAt,
          endsAt,
        });
      }
      return adminApi.updateBanner(editingId, {
        message: form.message.trim(),
        severity: form.severity,
        isAnimated: form.isAnimated,
        marqueeSpeed: form.marqueeSpeed,
        startsAt,
        endsAt,
      });
    },
    onSuccess: () => {
      invalidateAll();
      resetForm();
    },
  });

  const setActive = useMutation({
    mutationFn: (v: { id: number; isActive: boolean }) =>
      adminApi.setBannerActive(v.id, v.isActive),
    onSuccess: () => invalidateAll(),
  });

  const remove = useMutation({
    mutationFn: (id: number) => adminApi.deleteBanner(id),
    onSuccess: (_data, deletedId) => {
      setConfirmDeleteId(null);
      // If the row being edited was deleted, drop the composer back to create mode.
      if (editingId === deletedId) resetForm();
      invalidateAll();
    },
  });

  function submit() {
    const message = form.message.trim();
    if (!message) {
      setFormError("Enter a message for the banner.");
      return;
    }
    const startsAt = localInputToIso(form.startsLocal);
    const endsAt = localInputToIso(form.endsLocal);
    if (startsAt && endsAt && new Date(endsAt) <= new Date(startsAt)) {
      setFormError("The end time must be after the start time.");
      return;
    }
    setFormError(null);
    save.mutate();
  }

  function startEdit(b: StatusBanner) {
    save.reset(); // clear any stale save error from a previous edit
    setEditingId(b.bannerId);
    setForm({
      message: b.message,
      severity: b.severity,
      isAnimated: b.isAnimated,
      marqueeSpeed: b.marqueeSpeed,
      isActive: b.isActive,
      startsLocal: isoToLocalInput(b.startsAt),
      endsLocal: isoToLocalInput(b.endsAt),
    });
    setFormError(null);
    if (typeof window !== "undefined") window.scrollTo({ top: 0, behavior: "smooth" });
  }

  if (!isHydrated || !user) {
    return (
      <div className="min-h-[60vh] flex items-center justify-center">
        <Spinner size={28} />
      </div>
    );
  }

  const rows = banners.data ?? [];

  return (
    <div className="mx-auto max-w-5xl px-6 py-8">
      <div className="mb-6">
        <p className="text-[10px] uppercase tracking-[0.3em] text-[color:var(--color-accent)]">
          Admin
        </p>
        <h1 className="text-3xl mt-1" style={{ fontFamily: "var(--font-display)" }}>
          Status banner
        </h1>
        <p className="text-sm text-[color:var(--color-muted-fg)] mt-1">
          Post a notice across the top of the app, for example when PACS or the RIS is
          down. Everyone signed in sees the active banner; only NRS and Admin can post.
        </p>
      </div>

      {/* Composer ---------------------------------------------------------- */}
      <div className="rounded-lg border border-[color:var(--color-border)] bg-[color:var(--color-surface)] p-5 mb-8">
        <div className="flex items-center justify-between mb-4">
          <h2 className="text-sm font-medium">
            {editingId == null ? "New banner" : `Editing banner #${editingId}`}
          </h2>
          {editingId != null ? (
            <Button
              variant="ghost"
              size="sm"
              onClick={() => {
                save.reset(); // clear any stale save error when leaving edit mode
                resetForm();
              }}
            >
              Cancel edit
            </Button>
          ) : null}
        </div>

        {/* Live preview */}
        <div className="mb-4">
          <p className="text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)] mb-1.5">
            Preview
          </p>
          <div className="rounded-md border border-[color:var(--color-border)] overflow-hidden">
            <BannerView
              message={form.message}
              severity={form.severity}
              isAnimated={form.isAnimated}
              speed={form.marqueeSpeed}
            />
          </div>
        </div>

        <label
          htmlFor="banner-message"
          className="block text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)] mb-1.5"
        >
          Message
        </label>
        <textarea
          id="banner-message"
          value={form.message}
          onChange={(e) => setForm((f) => ({ ...f, message: e.target.value }))}
          rows={2}
          maxLength={2000}
          placeholder="e.g. PACS is down. We are working to restore it."
          className="w-full rounded-md border border-[color:var(--color-border)] bg-[color:var(--color-surface)] px-3 py-2 text-sm resize-y focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-accent)]/60"
        />

        <div className="mt-4 flex flex-wrap items-end gap-x-6 gap-y-4">
          <div>
            <label
              htmlFor="banner-severity"
              className="block text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)] mb-1.5"
            >
              Severity
            </label>
            <select
              id="banner-severity"
              value={form.severity}
              onChange={(e) => setForm((f) => ({ ...f, severity: Number(e.target.value) }))}
              className="h-10 rounded-md border border-[color:var(--color-border)] bg-[color:var(--color-surface)] px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-accent)]/60"
            >
              {SEVERITY_OPTIONS.map((o) => (
                <option key={o.value} value={o.value}>
                  {o.label}
                </option>
              ))}
            </select>
          </div>

          <div>
            <label
              htmlFor="banner-starts"
              className="block text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)] mb-1.5"
            >
              Show from (optional)
            </label>
            <input
              id="banner-starts"
              type="datetime-local"
              value={form.startsLocal}
              onChange={(e) => setForm((f) => ({ ...f, startsLocal: e.target.value }))}
              className="h-10 rounded-md border border-[color:var(--color-border)] bg-[color:var(--color-surface)] px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-accent)]/60"
            />
          </div>

          <div>
            <label
              htmlFor="banner-ends"
              className="block text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)] mb-1.5"
            >
              Hide after (optional)
            </label>
            <input
              id="banner-ends"
              type="datetime-local"
              value={form.endsLocal}
              onChange={(e) => setForm((f) => ({ ...f, endsLocal: e.target.value }))}
              className="h-10 rounded-md border border-[color:var(--color-border)] bg-[color:var(--color-surface)] px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-accent)]/60"
            />
          </div>

          <label className="inline-flex items-center gap-2 text-sm cursor-pointer select-none h-10">
            <input
              type="checkbox"
              checked={form.isAnimated}
              onChange={(e) => setForm((f) => ({ ...f, isAnimated: e.target.checked }))}
              className="size-4 accent-[color:var(--color-accent)]"
            />
            <span>Animated</span>
          </label>

          {form.isAnimated ? (
            <div className="flex items-center gap-2 h-10">
              <label
                htmlFor="banner-speed"
                className="text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)]"
              >
                Speed
              </label>
              <input
                id="banner-speed"
                type="range"
                min={1}
                max={10}
                step={1}
                value={form.marqueeSpeed}
                onChange={(e) =>
                  setForm((f) => ({ ...f, marqueeSpeed: Number(e.target.value) }))
                }
                className="w-32 accent-[color:var(--color-accent)]"
                aria-label="Marquee scroll speed, 1 slow to 10 fast"
              />
              <span className="text-xs tabular-nums text-[color:var(--color-muted-fg)] w-5">
                {form.marqueeSpeed}
              </span>
            </div>
          ) : null}

          {editingId == null ? (
            <label className="inline-flex items-center gap-2 text-sm cursor-pointer select-none h-10">
              <input
                type="checkbox"
                checked={form.isActive}
                onChange={(e) => setForm((f) => ({ ...f, isActive: e.target.checked }))}
                className="size-4 accent-[color:var(--color-accent)]"
              />
              <span>Active now</span>
            </label>
          ) : null}
        </div>

        {formError ? (
          <p className="mt-3 text-sm text-[color:var(--color-novarad-red)]">{formError}</p>
        ) : null}
        {save.isError ? (
          <p className="mt-3 text-sm text-[color:var(--color-novarad-red)]">
            Couldn&apos;t save the banner. Try again, or refresh if it keeps failing.
          </p>
        ) : null}

        <div className="mt-4 flex items-center gap-3">
          <Button onClick={submit} loading={save.isPending}>
            {editingId == null ? "Post banner" : "Save changes"}
          </Button>
          <span className="text-xs text-[color:var(--color-muted-fg)]">
            {editingId == null
              ? "Leave “Show from / Hide after” blank to show it immediately and until you turn it off."
              : "Activation is managed from the list below."}
          </span>
        </div>
      </div>

      {/* List -------------------------------------------------------------- */}
      <div className="flex items-center justify-between mb-3">
        <h2 className="text-sm font-medium">Banners</h2>
        {banners.isFetching ? <Spinner size={16} /> : null}
      </div>

      {banners.isError ? (
        <ErrorBar label="Couldn't load the banners." onRetry={() => banners.refetch()} />
      ) : null}

      <div className="rounded-lg border border-[color:var(--color-border)] bg-[color:var(--color-surface)] overflow-hidden">
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead className="text-left text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)] bg-[color:var(--color-surface-2)]">
              <tr>
                <th className="px-4 py-3 font-medium w-24">Status</th>
                <th className="px-4 py-3 font-medium w-32">Severity</th>
                <th className="px-4 py-3 font-medium">Message</th>
                <th className="px-4 py-3 font-medium w-56">Schedule</th>
                <th className="px-4 py-3 font-medium w-64 text-right">Actions</th>
              </tr>
            </thead>
            <tbody>
              {banners.isLoading ? (
                <tr>
                  <td colSpan={5} className="px-4 py-10 text-center text-[color:var(--color-muted-fg)]">
                    <Spinner size={20} />
                  </td>
                </tr>
              ) : rows.length === 0 && !banners.isError ? (
                <tr>
                  <td colSpan={5} className="px-4 py-10 text-center text-sm text-[color:var(--color-muted-fg)]">
                    No banners yet. Post one above to notify everyone signed in.
                  </td>
                </tr>
              ) : (
                rows.map((b) => (
                  <BannerRow
                    key={b.bannerId}
                    banner={b}
                    busy={
                      (setActive.isPending && setActive.variables?.id === b.bannerId) ||
                      (remove.isPending && remove.variables === b.bannerId)
                    }
                    confirmingDelete={confirmDeleteId === b.bannerId}
                    onEdit={() => startEdit(b)}
                    onToggleActive={() =>
                      setActive.mutate({ id: b.bannerId, isActive: !b.isActive })
                    }
                    onAskDelete={() => setConfirmDeleteId(b.bannerId)}
                    onCancelDelete={() => setConfirmDeleteId(null)}
                    onConfirmDelete={() => remove.mutate(b.bannerId)}
                  />
                ))
              )}
            </tbody>
          </table>
        </div>
      </div>

      {setActive.isError || remove.isError ? (
        <p className="mt-3 text-sm text-[color:var(--color-novarad-red)]">
          Couldn&apos;t update that banner. Try again, or refresh if it keeps failing.
        </p>
      ) : null}
    </div>
  );
}

function scheduleLabel(b: StatusBanner): string {
  if (!b.startsAt && !b.endsAt) return "Always";
  const from = b.startsAt ? formatDateTime(b.startsAt) : "now";
  const to = b.endsAt ? formatDateTime(b.endsAt) : "until cleared";
  return `${from} → ${to}`;
}

function severityVariant(
  severity: number,
): "accent" | "caution" | "danger" | "neutral" {
  if (severity >= BannerSeverity.Critical) return "danger";
  if (severity === BannerSeverity.Warning) return "caution";
  if (severity === BannerSeverity.Maintenance) return "neutral";
  return "accent";
}

function BannerRow({
  banner,
  busy,
  confirmingDelete,
  onEdit,
  onToggleActive,
  onAskDelete,
  onCancelDelete,
  onConfirmDelete,
}: {
  banner: StatusBanner;
  busy: boolean;
  confirmingDelete: boolean;
  onEdit: () => void;
  onToggleActive: () => void;
  onAskDelete: () => void;
  onCancelDelete: () => void;
  onConfirmDelete: () => void;
}) {
  return (
    <tr className="border-t border-[color:var(--color-border)] hover:bg-[color:var(--color-surface-2)]/50 align-top">
      <td className="px-4 py-3">
        <Badge variant={banner.isActive ? "success" : "neutral"}>
          {banner.isActive ? "Active" : "Off"}
        </Badge>
      </td>
      <td className="px-4 py-3">
        <Badge variant={severityVariant(banner.severity)}>
          {SEVERITY_LABEL[banner.severity] ?? "Info"}
        </Badge>
      </td>
      <td className="px-4 py-3 max-w-md">
        <span className="line-clamp-2" title={banner.message}>
          {banner.message}
        </span>
      </td>
      <td className="px-4 py-3 text-xs text-[color:var(--color-muted-fg)]">
        {scheduleLabel(banner)}
      </td>
      <td className="px-4 py-3">
        <div className="flex items-center justify-end gap-2">
          {busy ? <Spinner size={14} /> : null}
          {confirmingDelete ? (
            <>
              <span className="text-xs text-[color:var(--color-muted-fg)]">Delete?</span>
              <Button variant="danger" size="sm" onClick={onConfirmDelete} disabled={busy}>
                <Check className="size-4" /> Confirm
              </Button>
              <Button variant="ghost" size="sm" onClick={onCancelDelete} disabled={busy}>
                <X className="size-4" /> Cancel
              </Button>
            </>
          ) : (
            <>
              <Button variant="ghost" size="sm" onClick={onEdit} disabled={busy}>
                <Pencil className="size-3.5" /> Edit
              </Button>
              <Button variant="secondary" size="sm" onClick={onToggleActive} disabled={busy}>
                {banner.isActive ? "Deactivate" : "Activate"}
              </Button>
              <Button variant="ghost" size="sm" onClick={onAskDelete} disabled={busy}>
                <X className="size-4" /> Delete
              </Button>
            </>
          )}
        </div>
      </td>
    </tr>
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
