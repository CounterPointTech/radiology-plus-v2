"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AnimatePresence, motion } from "framer-motion";
import {
  CalendarClock,
  ChevronDown,
  Clock,
  Pencil,
  Play,
  Square,
} from "lucide-react";
import Link from "next/link";
import { useParams, useRouter } from "next/navigation";
import { useEffect, useState } from "react";

import { CodeEditor } from "@/components/scripts/code-editor";
import { formatDuration, LanguageBadge, RunStatus, TargetBadge } from "@/components/scripts/script-bits";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Spinner } from "@/components/ui/spinner";
import { scriptsApi } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { describeCron } from "@/lib/cron";
import type { ScriptExecutionListItem } from "@/lib/types";
import { canAccessScripting } from "@/lib/types";
import { formatDateTime } from "@/lib/utils";

export default function ScriptDetailPage() {
  const router = useRouter();
  const qc = useQueryClient();
  const { scriptId } = useParams<{ scriptId: string }>();
  const { user, isHydrated } = useAuth();
  const [showBody, setShowBody] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);

  useEffect(() => {
    if (isHydrated && user && !canAccessScripting(user.role)) {
      router.replace("/notifications");
    }
  }, [isHydrated, user, router]);

  const enabled = !!user && canAccessScripting(user.role);
  const script = useQuery({
    queryKey: ["script", scriptId],
    queryFn: () => scriptsApi.get(scriptId),
    enabled,
  });

  // The live run console: refetch fast while anything is in flight.
  const executions = useQuery({
    queryKey: ["script-executions", scriptId],
    queryFn: () => scriptsApi.executionsFor(scriptId, { limit: 25 }),
    enabled,
    refetchInterval: (query) =>
      (query.state.data ?? []).some((e) => e.status === "pending" || e.status === "running")
        ? 2_000
        : false,
  });

  const runMut = useMutation({
    mutationFn: () => scriptsApi.run(scriptId),
    onSuccess: () => {
      setNotice(null);
      // Poll almost immediately so the new pending row appears.
      window.setTimeout(() => {
        qc.invalidateQueries({ queryKey: ["script-executions", scriptId] });
      }, 400);
    },
    onError: () => setNotice("Couldn't start the run. Try again."),
  });

  const cancelMut = useMutation({
    mutationFn: (executionId: number) => scriptsApi.cancel(executionId),
    onSuccess: (r) => {
      setNotice(r.cancelled ? null : r.message);
      qc.invalidateQueries({ queryKey: ["script-executions", scriptId] });
    },
  });

  if (!isHydrated || !user || script.isLoading) {
    return (
      <div className="min-h-[60vh] flex items-center justify-center">
        <Spinner size={28} />
      </div>
    );
  }

  if (script.isError || !script.data) {
    return (
      <div className="mx-auto max-w-5xl px-6 py-16 text-center">
        <p className="text-sm text-[color:var(--color-muted-fg)]">
          Couldn&apos;t load that script — it may have been deleted.
        </p>
        <Link
          href="/scripts"
          className="mt-3 inline-block text-sm text-[color:var(--color-accent)] underline underline-offset-2"
        >
          Back to Script Manager
        </Link>
      </div>
    );
  }

  const s = script.data;
  const schedule = describeCron(s.cronExpression);
  const anyLive = (executions.data ?? []).some(
    (e) => e.status === "pending" || e.status === "running",
  );

  return (
    <div className="mx-auto max-w-5xl px-6 py-8">
      <div className="mb-6 rise-in">
        <p className="text-[10px] uppercase tracking-[0.3em] text-[color:var(--color-accent)]">
          <Link href="/scripts" className="hover:underline underline-offset-2">
            Script Manager
          </Link>
        </p>
        <div className="mt-1 flex flex-wrap items-center gap-3">
          <h1 className="text-3xl" style={{ fontFamily: "var(--font-display)" }}>
            {s.name}
          </h1>
          <LanguageBadge language={s.language} />
          <TargetBadge target={s.connectionTarget} />
          {s.isActive ? null : <Badge variant="neutral">inactive</Badge>}
        </div>
        {s.description ? (
          <p className="text-sm text-[color:var(--color-muted-fg)] mt-1 max-w-2xl">
            {s.description}
          </p>
        ) : null}
        <div className="mt-2 flex flex-wrap items-center gap-x-4 gap-y-1 text-xs text-[color:var(--color-muted-fg)]">
          {s.cronExpression ? (
            <span className="inline-flex items-center gap-1">
              <CalendarClock className="size-3.5" />
              {schedule ?? <code className="font-mono">{s.cronExpression}</code>}
              {s.nextRunAt ? ` · next ${formatDateTime(s.nextRunAt)}` : ""}
            </span>
          ) : (
            <span>on demand</span>
          )}
          <span className="inline-flex items-center gap-1">
            <Clock className="size-3.5" />
            timeout {s.timeoutSeconds}s
          </span>
        </div>

        <div className="mt-4 flex items-center gap-2">
          <Button
            loading={runMut.isPending}
            disabled={!s.isActive || anyLive}
            onClick={() => runMut.mutate()}
            title={
              !s.isActive
                ? "Activate the script to run it"
                : anyLive
                  ? "A run is already in flight"
                  : "Run now"
            }
          >
            <Play className="size-4" />
            Run now
          </Button>
          <Link
            href={`/scripts/${scriptId}/edit` as never}
            className="inline-flex h-10 items-center gap-2 rounded-md border border-[color:var(--color-border)] bg-[color:var(--color-surface)] px-4 text-sm font-medium hover:bg-[color:var(--color-surface-2)] transition-colors"
          >
            <Pencil className="size-4" />
            Edit
          </Link>
          <button
            type="button"
            onClick={() => setShowBody((v) => !v)}
            className="inline-flex items-center gap-1.5 px-2 text-sm text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-base-fg)] transition-colors"
          >
            <ChevronDown
              className={`size-4 transition-transform ${showBody ? "rotate-180" : ""}`}
            />
            {showBody ? "Hide body" : "Show body"}
          </button>
        </div>
      </div>

      {notice ? <p className="mb-4 text-sm text-[color:var(--color-caution)]">{notice}</p> : null}

      <AnimatePresence>
        {showBody ? (
          <motion.div
            initial={{ opacity: 0, height: 0 }}
            animate={{ opacity: 1, height: "auto" }}
            exit={{ opacity: 0, height: 0 }}
            className="overflow-hidden mb-6"
          >
            <CodeEditor value={s.body} language={s.language} readOnly minHeight="6rem" maxHeight="24rem" />
          </motion.div>
        ) : null}
      </AnimatePresence>

      <h2 className="text-sm font-medium text-[color:var(--color-muted-fg)] mb-2 flex items-center gap-2">
        Runs
        {anyLive ? <Spinner size={12} /> : null}
      </h2>
      {executions.isLoading ? (
        <Spinner size={18} />
      ) : (executions.data ?? []).length === 0 ? (
        <p className="text-sm text-[color:var(--color-muted-fg)]">
          No runs yet — press <strong>Run now</strong> to watch the first one live.
        </p>
      ) : (
        <div className="rounded-lg border border-[color:var(--color-border)] bg-[color:var(--color-surface)] divide-y divide-[color:var(--color-border)]/60">
          <AnimatePresence initial={false}>
            {(executions.data ?? []).map((e) => (
              <ExecutionRow
                key={e.executionId}
                execution={e}
                onCancel={() => cancelMut.mutate(e.executionId)}
                cancelling={cancelMut.isPending && cancelMut.variables === e.executionId}
              />
            ))}
          </AnimatePresence>
        </div>
      )}
    </div>
  );
}

function ExecutionRow({
  execution: e,
  onCancel,
  cancelling,
}: {
  execution: ScriptExecutionListItem;
  onCancel: () => void;
  cancelling: boolean;
}) {
  const [open, setOpen] = useState(false);
  const live = e.status === "pending" || e.status === "running";

  const detail = useQuery({
    queryKey: ["script-execution", e.executionId],
    queryFn: () => scriptsApi.execution(e.executionId),
    enabled: open,
    // While open on a live run, keep the logs fresh too.
    refetchInterval: open && live ? 2_000 : false,
  });

  return (
    <motion.div
      layout
      initial={{ opacity: 0, y: -8 }}
      animate={{ opacity: 1, y: 0 }}
      className="px-4 py-2.5"
    >
      <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-sm">
        <span className="w-14 font-mono text-xs text-[color:var(--color-muted-fg)]">
          #{e.executionId}
        </span>
        <RunStatus status={e.status} />
        <span className="text-xs text-[color:var(--color-muted-fg)] tabular-nums">
          {formatDateTime(e.startedAt ?? e.createdAt)}
        </span>
        <span className="text-xs text-[color:var(--color-muted-fg)]">
          {e.triggeredBy}
          {e.durationMs != null ? ` · ${formatDuration(e.durationMs)}` : ""}
          {e.rowsAffected != null ? ` · ${e.rowsAffected} row${e.rowsAffected === 1 ? "" : "s"}` : ""}
        </span>
        <span className="ml-auto flex items-center gap-2">
          {live ? (
            <Button variant="ghost" size="sm" loading={cancelling} onClick={onCancel}>
              <Square className="size-3.5" />
              Cancel
            </Button>
          ) : null}
          <button
            type="button"
            onClick={() => setOpen((v) => !v)}
            aria-label={`Toggle logs for run ${e.executionId}`}
            className="inline-flex items-center gap-1 text-xs text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-accent)] transition-colors"
          >
            <ChevronDown className={`size-4 transition-transform ${open ? "rotate-180" : ""}`} />
            logs
          </button>
        </span>
      </div>

      <AnimatePresence>
        {open ? (
          <motion.div
            initial={{ opacity: 0, height: 0 }}
            animate={{ opacity: 1, height: "auto" }}
            exit={{ opacity: 0, height: 0 }}
            className="overflow-hidden"
          >
            <div className="mt-2 space-y-2">
              {detail.isLoading ? (
                <Spinner size={14} />
              ) : detail.data ? (
                <>
                  {detail.data.outputLog ? (
                    <pre className="rounded-md bg-[color:var(--color-surface-2)] p-3 text-xs font-mono whitespace-pre-wrap max-h-72 overflow-y-auto">
                      {detail.data.outputLog}
                    </pre>
                  ) : null}
                  {detail.data.errorLog ? (
                    <pre className="rounded-md border border-[color:var(--color-novarad-red)]/40 bg-[color:var(--color-novarad-red)]/10 p-3 text-xs font-mono whitespace-pre-wrap text-[color:var(--color-novarad-red)] max-h-72 overflow-y-auto">
                      {detail.data.errorLog}
                    </pre>
                  ) : null}
                  {!detail.data.outputLog && !detail.data.errorLog ? (
                    <p className="text-xs text-[color:var(--color-muted-fg)]">
                      {live ? "Waiting for output…" : "This run produced no output."}
                    </p>
                  ) : null}
                </>
              ) : null}
            </div>
          </motion.div>
        ) : null}
      </AnimatePresence>
    </motion.div>
  );
}
