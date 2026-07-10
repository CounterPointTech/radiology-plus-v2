"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { motion } from "framer-motion";
import {
  CalendarClock,
  Copy,
  FlaskConical,
  Pencil,
  Play,
  Plus,
  Terminal,
  Trash2,
} from "lucide-react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";

import { formatDuration, LanguageBadge, RunStatus, TargetBadge } from "@/components/scripts/script-bits";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Spinner } from "@/components/ui/spinner";
import { adminApi, scriptsApi } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { describeCron } from "@/lib/cron";
import type { ScriptSummary } from "@/lib/types";
import { canAccessScripting } from "@/lib/types";
import { formatDateTime } from "@/lib/utils";

export default function ScriptsPage() {
  const router = useRouter();
  const qc = useQueryClient();
  const { user, isHydrated } = useAuth();
  const [confirmDeleteId, setConfirmDeleteId] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);

  // Script Manager is NRS-only; Admin-role users get bounced to Notifications.
  useEffect(() => {
    if (isHydrated && user && !canAccessScripting(user.role)) {
      router.replace("/notifications");
    }
  }, [isHydrated, user, router]);

  const scripts = useQuery({
    queryKey: ["scripts"],
    queryFn: () => scriptsApi.list(),
    enabled: !!user && canAccessScripting(user.role),
  });
  const invalidate = () => qc.invalidateQueries({ queryKey: ["scripts"] });

  const runMut = useMutation({
    mutationFn: (id: string) => scriptsApi.run(id),
    onSuccess: (_r, id) => {
      invalidate();
      router.push(`/scripts/${id}` as never); // watch it in the live console
    },
    onError: () => setActionError("Couldn't start the run. Try again."),
  });
  const toggleMut = useMutation({
    mutationFn: (v: { id: string; isActive: boolean }) => scriptsApi.setActive(v.id, v.isActive),
    onSuccess: invalidate,
    onError: () => setActionError("Couldn't update the script. Try again."),
  });
  const deleteMut = useMutation({
    mutationFn: (id: string) => scriptsApi.remove(id),
    onSuccess: () => {
      setConfirmDeleteId(null);
      invalidate();
    },
    onError: () => setActionError("Couldn't delete the script — it may be used by a chain."),
  });
  const smokeMut = useMutation({ mutationFn: () => adminApi.runScriptSmokeTest() });

  if (!isHydrated || !user) {
    return (
      <div className="min-h-[60vh] flex items-center justify-center">
        <Spinner size={28} />
      </div>
    );
  }

  const rows = scripts.data ?? [];

  return (
    <div className="mx-auto max-w-6xl px-6 py-8">
      <div className="mb-6 flex flex-wrap items-end justify-between gap-4 rise-in">
        <div>
          <p className="text-[10px] uppercase tracking-[0.3em] text-[color:var(--color-accent)]">
            Technical
          </p>
          <h1 className="text-3xl mt-1" style={{ fontFamily: "var(--font-display)" }}>
            Script Manager<span className="caret-blink text-[color:var(--color-accent)]">▍</span>
          </h1>
          <p className="text-sm text-[color:var(--color-muted-fg)] mt-1 max-w-2xl">
            Author, schedule, and run workflow scripts against Novarad, M*Modal, or the
            Radiology Plus database.
          </p>
        </div>
        <div className="flex items-center gap-2">
          <Button
            variant="ghost"
            size="sm"
            loading={smokeMut.isPending}
            onClick={() => smokeMut.mutate()}
            title="Run an inline SELECT 1 through the engine"
          >
            <FlaskConical className="size-4" />
            Engine check
          </Button>
          <Button variant="primary" size="sm" onClick={() => router.push("/scripts/new" as never)}>
            <Plus className="size-4" />
            New script
          </Button>
        </div>
      </div>

      {smokeMut.data ? (
        <p
          className={`mb-4 text-sm ${smokeMut.data.ok ? "text-[color:var(--color-success)]" : "text-[color:var(--color-novarad-red)]"}`}
        >
          Engine {smokeMut.data.ok ? `OK — ${smokeMut.data.durationMs}ms` : `check failed: ${smokeMut.data.error}`}
        </p>
      ) : null}
      {actionError ? (
        <p className="mb-4 text-sm text-[color:var(--color-novarad-red)]">{actionError}</p>
      ) : null}

      {scripts.isLoading ? (
        <div className="min-h-[30vh] flex items-center justify-center">
          <Spinner size={24} />
        </div>
      ) : scripts.isError ? (
        <p className="text-sm text-[color:var(--color-novarad-red)]">
          Couldn&apos;t load scripts.{" "}
          <button className="underline underline-offset-2" onClick={() => scripts.refetch()}>
            Try again
          </button>
        </p>
      ) : rows.length === 0 ? (
        <div className="rounded-lg border border-dashed border-[color:var(--color-border)] px-6 py-16 text-center rise-in">
          <Terminal className="size-8 mx-auto text-[color:var(--color-accent)]" />
          <p className="mt-3 text-sm text-[color:var(--color-muted-fg)]">
            No scripts yet. Create the first one — it can run on a schedule or on demand.
          </p>
          <Button className="mt-4" size="sm" onClick={() => router.push("/scripts/new" as never)}>
            <Plus className="size-4" />
            New script
          </Button>
        </div>
      ) : (
        <motion.ul
          initial="hidden"
          animate="show"
          variants={{ hidden: {}, show: { transition: { staggerChildren: 0.06 } } }}
          className="space-y-3"
        >
          {rows.map((s) => (
            <ScriptRow
              key={s.scriptId}
              script={s}
              busy={
                (runMut.isPending && runMut.variables === s.scriptId) ||
                (toggleMut.isPending && toggleMut.variables?.id === s.scriptId) ||
                (deleteMut.isPending && deleteMut.variables === s.scriptId)
              }
              confirmingDelete={confirmDeleteId === s.scriptId}
              onRun={() => runMut.mutate(s.scriptId)}
              onToggle={() => toggleMut.mutate({ id: s.scriptId, isActive: !s.isActive })}
              onAskDelete={() => setConfirmDeleteId(s.scriptId)}
              onCancelDelete={() => setConfirmDeleteId(null)}
              onConfirmDelete={() => deleteMut.mutate(s.scriptId)}
            />
          ))}
        </motion.ul>
      )}
    </div>
  );
}

function ScriptRow({
  script: s,
  busy,
  confirmingDelete,
  onRun,
  onToggle,
  onAskDelete,
  onCancelDelete,
  onConfirmDelete,
}: {
  script: ScriptSummary;
  busy: boolean;
  confirmingDelete: boolean;
  onRun: () => void;
  onToggle: () => void;
  onAskDelete: () => void;
  onCancelDelete: () => void;
  onConfirmDelete: () => void;
}) {
  const schedule = describeCron(s.cronExpression);

  return (
    <motion.li
      variants={{ hidden: { opacity: 0, y: 14 }, show: { opacity: 1, y: 0 } }}
      whileHover={{ y: -2 }}
      transition={{ type: "spring", stiffness: 400, damping: 28 }}
      className="rounded-lg border border-[color:var(--color-border)] bg-[color:var(--color-surface)] px-4 py-3 hover:border-[color:var(--color-accent)]/40 hover:shadow-[var(--glow-accent)] transition-[border-color,box-shadow]"
    >
      <div className="flex flex-wrap items-center gap-x-4 gap-y-2">
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <Link
              href={`/scripts/${s.scriptId}` as never}
              className="font-medium hover:text-[color:var(--color-accent)] transition-colors truncate"
              style={{ fontFamily: "var(--font-display)" }}
            >
              {s.name}
            </Link>
            <LanguageBadge language={s.language} />
            <TargetBadge target={s.connectionTarget} />
            {s.isActive ? null : <Badge variant="neutral">inactive</Badge>}
          </div>
          {s.description ? (
            <p className="mt-0.5 text-xs text-[color:var(--color-muted-fg)] truncate">
              {s.description}
            </p>
          ) : null}
          <div className="mt-1.5 flex flex-wrap items-center gap-x-4 gap-y-1 text-xs text-[color:var(--color-muted-fg)]">
            {s.cronExpression ? (
              <span className="inline-flex items-center gap-1">
                <CalendarClock className="size-3.5" />
                {schedule ?? <code className="font-mono">{s.cronExpression}</code>}
                {s.nextRunAt ? ` · next ${formatDateTime(s.nextRunAt)}` : ""}
              </span>
            ) : (
              <span>on demand</span>
            )}
            <span className="inline-flex items-center gap-1.5">
              <RunStatus status={s.lastStatus} />
              {s.lastStartedAt ? (
                <>
                  {formatDateTime(s.lastStartedAt)} · {formatDuration(s.lastDurationMs)}
                </>
              ) : null}
            </span>
          </div>
        </div>

        <div className="flex items-center gap-1.5">
          {busy ? <Spinner size={14} /> : null}
          {confirmingDelete ? (
            <>
              <span className="text-xs text-[color:var(--color-caution)]">Delete this script?</span>
              <Button variant="danger" size="sm" onClick={onConfirmDelete} disabled={busy}>
                Confirm
              </Button>
              <Button variant="ghost" size="sm" onClick={onCancelDelete} disabled={busy}>
                Cancel
              </Button>
            </>
          ) : (
            <>
              <Button variant="secondary" size="sm" onClick={onRun} disabled={busy || !s.isActive}
                title={s.isActive ? "Run now" : "Activate the script to run it"}>
                <Play className="size-3.5" />
                Run
              </Button>
              <Button variant="ghost" size="sm" onClick={onToggle} disabled={busy}>
                {s.isActive ? "Deactivate" : "Activate"}
              </Button>
              <Link
                href={`/scripts/${s.scriptId}/edit` as never}
                className="inline-flex items-center gap-1 rounded-md px-2.5 py-1.5 text-sm text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-base-fg)] hover:bg-[color:var(--color-surface-2)] transition-colors"
              >
                <Pencil className="size-3.5" /> Edit
              </Link>
              <Link
                href={`/scripts/new?from=${s.scriptId}` as never}
                title="Duplicate"
                aria-label={`Duplicate ${s.name}`}
                className="inline-flex items-center rounded-md p-1.5 text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-accent)] hover:bg-[color:var(--color-surface-2)] transition-colors"
              >
                <Copy className="size-3.5" />
              </Link>
              <button
                type="button"
                onClick={onAskDelete}
                disabled={busy}
                title="Delete"
                aria-label={`Delete ${s.name}`}
                className="inline-flex items-center rounded-md p-1.5 text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-novarad-red)] hover:bg-[color:var(--color-surface-2)] transition-colors disabled:opacity-50"
              >
                <Trash2 className="size-3.5" />
              </button>
            </>
          )}
        </div>
      </div>
    </motion.li>
  );
}
