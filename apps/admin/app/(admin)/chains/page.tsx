"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { motion } from "framer-motion";
import { BellRing, CalendarClock, Pencil, Play, Plus, Trash2, Workflow } from "lucide-react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";

import { formatDuration, RunStatus } from "@/components/scripts/script-bits";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Spinner } from "@/components/ui/spinner";
import { chainsApi } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { describeCron } from "@/lib/cron";
import type { ChainSummary } from "@/lib/types";
import { canAccessScripting } from "@/lib/types";
import { formatDateTime } from "@/lib/utils";

export default function ChainsPage() {
  const router = useRouter();
  const qc = useQueryClient();
  const { user, isHydrated } = useAuth();
  const [confirmDeleteId, setConfirmDeleteId] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);

  // Chains are NRS-only, like the Script Manager.
  useEffect(() => {
    if (isHydrated && user && !canAccessScripting(user.role)) {
      router.replace("/notifications");
    }
  }, [isHydrated, user, router]);

  const chains = useQuery({
    queryKey: ["chains"],
    queryFn: () => chainsApi.list(),
    enabled: !!user && canAccessScripting(user.role),
  });
  const invalidate = () => qc.invalidateQueries({ queryKey: ["chains"] });

  const runMut = useMutation({
    mutationFn: (id: string) => chainsApi.run(id),
    onSuccess: (_r, id) => {
      void invalidate();
      router.push(`/chains/${id}` as never); // detail auto-follows the newest run
    },
    onError: () => setActionError("Couldn't start the run. Try again."),
  });
  const toggleMut = useMutation({
    mutationFn: (v: { id: string; isActive: boolean }) => chainsApi.setActive(v.id, v.isActive),
    onSuccess: invalidate,
    onError: () => setActionError("Couldn't update the chain. Try again."),
  });
  const deleteMut = useMutation({
    mutationFn: (id: string) => chainsApi.remove(id),
    onSuccess: () => {
      setConfirmDeleteId(null);
      void invalidate();
    },
    onError: () => setActionError("Couldn't delete the chain. Try again."),
  });

  if (!isHydrated || !user) {
    return (
      <div className="min-h-[60vh] flex items-center justify-center">
        <Spinner size={28} />
      </div>
    );
  }

  const rows = chains.data ?? [];

  return (
    <div className="mx-auto max-w-6xl px-6 py-8">
      <div className="mb-6 flex flex-wrap items-end justify-between gap-4 rise-in">
        <div>
          <p className="text-[10px] uppercase tracking-[0.3em] text-[color:var(--color-accent)]">
            Technical
          </p>
          <h1 className="text-3xl mt-1" style={{ fontFamily: "var(--font-display)" }}>
            Script Chains<span className="caret-blink text-[color:var(--color-accent)]">▍</span>
          </h1>
          <p className="text-sm text-[color:var(--color-muted-fg)] mt-1 max-w-2xl">
            Run several scripts in order as one unit — scheduled or on demand, with a
            failure policy per chain and per step.
          </p>
        </div>
        <Button variant="primary" size="sm" onClick={() => router.push("/chains/new" as never)}>
          <Plus className="size-4" />
          New chain
        </Button>
      </div>

      {actionError ? (
        <p className="mb-4 text-sm text-[color:var(--color-novarad-red)]">{actionError}</p>
      ) : null}

      {chains.isLoading ? (
        <div className="min-h-[30vh] flex items-center justify-center">
          <Spinner size={24} />
        </div>
      ) : chains.isError ? (
        <p className="text-sm text-[color:var(--color-novarad-red)]">
          Couldn&apos;t load chains.{" "}
          <button className="underline underline-offset-2" onClick={() => chains.refetch()}>
            Try again
          </button>
        </p>
      ) : rows.length === 0 ? (
        <div className="rounded-lg border border-dashed border-[color:var(--color-border)] px-6 py-16 text-center rise-in">
          <Workflow className="size-8 mx-auto text-[color:var(--color-accent)]" />
          <p className="mt-3 text-sm text-[color:var(--color-muted-fg)]">
            No chains yet. Create the first one from your existing scripts.
          </p>
          <Button className="mt-4" size="sm" onClick={() => router.push("/chains/new" as never)}>
            <Plus className="size-4" />
            New chain
          </Button>
        </div>
      ) : (
        <motion.ul
          initial="hidden"
          animate="show"
          variants={{ hidden: {}, show: { transition: { staggerChildren: 0.06 } } }}
          className="space-y-3"
        >
          {rows.map((c) => (
            <ChainRow
              key={c.chainId}
              chain={c}
              busy={
                (runMut.isPending && runMut.variables === c.chainId) ||
                (toggleMut.isPending && toggleMut.variables?.id === c.chainId) ||
                (deleteMut.isPending && deleteMut.variables === c.chainId)
              }
              confirmingDelete={confirmDeleteId === c.chainId}
              onRun={() => runMut.mutate(c.chainId)}
              onToggle={() => toggleMut.mutate({ id: c.chainId, isActive: !c.isActive })}
              onAskDelete={() => setConfirmDeleteId(c.chainId)}
              onCancelDelete={() => setConfirmDeleteId(null)}
              onConfirmDelete={() => deleteMut.mutate(c.chainId)}
            />
          ))}
        </motion.ul>
      )}
    </div>
  );
}

function ChainRow({
  chain: c,
  busy,
  confirmingDelete,
  onRun,
  onToggle,
  onAskDelete,
  onCancelDelete,
  onConfirmDelete,
}: {
  chain: ChainSummary;
  busy: boolean;
  confirmingDelete: boolean;
  onRun: () => void;
  onToggle: () => void;
  onAskDelete: () => void;
  onCancelDelete: () => void;
  onConfirmDelete: () => void;
}) {
  const schedule = describeCron(c.cronExpression);

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
              href={`/chains/${c.chainId}` as never}
              className="font-medium hover:text-[color:var(--color-accent)] transition-colors truncate"
              style={{ fontFamily: "var(--font-display)" }}
            >
              {c.name}
            </Link>
            <Badge variant="accent">
              {c.stepCount} step{c.stepCount === 1 ? "" : "s"}
            </Badge>
            <Badge variant="neutral">
              {c.onFailure === "stop" ? "stops on failure" : "keeps going"}
            </Badge>
            {c.notifiesOnFailure ? (
              <Badge variant="neutral">
                <BellRing className="size-3" /> on failure
              </Badge>
            ) : null}
            {c.isActive ? null : <Badge variant="neutral">inactive</Badge>}
          </div>
          {c.description ? (
            <p className="mt-0.5 text-xs text-[color:var(--color-muted-fg)] truncate">
              {c.description}
            </p>
          ) : null}
          <div className="mt-1.5 flex flex-wrap items-center gap-x-4 gap-y-1 text-xs text-[color:var(--color-muted-fg)]">
            {c.cronExpression ? (
              <span className="inline-flex items-center gap-1">
                <CalendarClock className="size-3.5" />
                {schedule ?? <code className="font-mono">{c.cronExpression}</code>}
                {c.nextRunAt ? ` · next ${formatDateTime(c.nextRunAt)}` : ""}
              </span>
            ) : (
              <span>on demand</span>
            )}
            <span className="inline-flex items-center gap-1.5">
              <RunStatus status={c.lastRunStatus} />
              {c.lastRunStartedAt ? (
                <>
                  {formatDateTime(c.lastRunStartedAt)} · {formatDuration(c.lastRunDurationMs)}
                </>
              ) : null}
            </span>
          </div>
        </div>

        <div className="flex items-center gap-1.5">
          {busy ? <Spinner size={14} /> : null}
          {confirmingDelete ? (
            <>
              <span className="text-xs text-[color:var(--color-caution)]">
                Delete this chain? Run history is kept on the scripts.
              </span>
              <Button variant="danger" size="sm" onClick={onConfirmDelete} disabled={busy}>
                Confirm
              </Button>
              <Button variant="ghost" size="sm" onClick={onCancelDelete} disabled={busy}>
                Cancel
              </Button>
            </>
          ) : (
            <>
              <Button
                variant="secondary"
                size="sm"
                onClick={onRun}
                disabled={busy || !c.isActive || c.stepCount === 0}
                title={
                  !c.isActive
                    ? "Activate the chain to run it"
                    : c.stepCount === 0
                      ? "Add steps first"
                      : "Run now"
                }
              >
                <Play className="size-3.5" />
                Run
              </Button>
              <Button variant="ghost" size="sm" onClick={onToggle} disabled={busy}>
                {c.isActive ? "Deactivate" : "Activate"}
              </Button>
              <Link
                href={`/chains/${c.chainId}/edit` as never}
                className="inline-flex items-center gap-1 rounded-md px-2.5 py-1.5 text-sm text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-base-fg)] hover:bg-[color:var(--color-surface-2)] transition-colors"
              >
                <Pencil className="size-3.5" /> Edit
              </Link>
              <button
                type="button"
                onClick={onAskDelete}
                disabled={busy}
                title="Delete"
                aria-label={`Delete ${c.name}`}
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
