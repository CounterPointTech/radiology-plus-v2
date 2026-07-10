"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AnimatePresence, motion } from "framer-motion";
import { CalendarClock, ChevronDown, Pencil, Play, Square } from "lucide-react";
import Link from "next/link";
import { useParams, useRouter } from "next/navigation";
import { useEffect, useState } from "react";

import { CopyId } from "@/components/notifications/notification-bits";
import { formatDuration, LanguageBadge, RunStatus } from "@/components/scripts/script-bits";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Spinner } from "@/components/ui/spinner";
import { chainsApi } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { describeCron } from "@/lib/cron";
import type { ChainDetail, ChainRunInfo } from "@/lib/types";
import { canAccessScripting } from "@/lib/types";
import { formatDateTime } from "@/lib/utils";

function isLive(status: string): boolean {
  return status === "pending" || status === "running";
}

export default function ChainDetailPage() {
  const router = useRouter();
  const qc = useQueryClient();
  const { chainId } = useParams<{ chainId: string }>();
  const { user, isHydrated } = useAuth();
  const [expandedRunId, setExpandedRunId] = useState<number | null>(null);
  const [note, setNote] = useState<string | null>(null);

  useEffect(() => {
    if (isHydrated && user && !canAccessScripting(user.role)) {
      router.replace("/notifications");
    }
  }, [isHydrated, user, router]);

  const chain = useQuery({
    queryKey: ["chain", chainId],
    queryFn: () => chainsApi.get(chainId),
    enabled: !!user && canAccessScripting(user.role),
  });

  const runs = useQuery({
    queryKey: ["chain-runs", chainId],
    queryFn: () => chainsApi.runsFor(chainId, { limit: 50 }),
    enabled: !!user && canAccessScripting(user.role),
    refetchInterval: (q) => (q.state.data?.some((r) => isLive(r.status)) ? 2_000 : 30_000),
  });

  // A freshly started run auto-expands so you watch it live without a click.
  useEffect(() => {
    const newest = runs.data?.[0];
    if (newest && isLive(newest.status)) setExpandedRunId(newest.chainRunId);
  }, [runs.data]);

  const runMut = useMutation({
    mutationFn: () => chainsApi.run(chainId),
    onSuccess: (r) => {
      setNote(null);
      setExpandedRunId(r.chainRunId);
      void qc.invalidateQueries({ queryKey: ["chain-runs", chainId] });
    },
    onError: () => setNote("Couldn't start the run. Try again."),
  });

  if (!isHydrated || !user || chain.isLoading) {
    return (
      <div className="min-h-[60vh] flex items-center justify-center">
        <Spinner size={28} />
      </div>
    );
  }

  if (chain.isError || !chain.data) {
    return (
      <div className="mx-auto max-w-4xl px-6 py-16 text-center">
        <p className="text-sm text-[color:var(--color-muted-fg)]">
          Couldn&apos;t load that chain — it may have been deleted.
        </p>
      </div>
    );
  }

  const c = chain.data;
  const schedule = describeCron(c.cronExpression);

  return (
    <div className="mx-auto max-w-5xl px-6 py-8">
      <div className="mb-6 flex flex-wrap items-end justify-between gap-4 rise-in">
        <div className="min-w-0">
          <p className="text-[10px] uppercase tracking-[0.3em] text-[color:var(--color-accent)]">
            <Link href="/chains" className="hover:underline underline-offset-2">
              Script Chains
            </Link>
          </p>
          <h1 className="text-3xl mt-1 truncate" style={{ fontFamily: "var(--font-display)" }}>
            {c.name}
          </h1>
          <div className="mt-1.5 flex flex-wrap items-center gap-2 text-xs text-[color:var(--color-muted-fg)]">
            <Badge variant="neutral">
              {c.onFailure === "stop" ? "stops on failure" : "keeps going on failure"}
            </Badge>
            {c.isActive ? null : <Badge variant="neutral">inactive</Badge>}
            {c.cronExpression ? (
              <span className="inline-flex items-center gap-1">
                <CalendarClock className="size-3.5" />
                {schedule ?? <code className="font-mono">{c.cronExpression}</code>}
                {c.nextRunAt ? ` · next ${formatDateTime(c.nextRunAt)}` : ""}
              </span>
            ) : (
              <span>on demand</span>
            )}
            {c.notifyOnFailureRecipient ? (
              <span>emails {c.notifyOnFailureRecipient} on failure</span>
            ) : null}
          </div>
          {c.description ? (
            <p className="mt-1 text-sm text-[color:var(--color-muted-fg)]">{c.description}</p>
          ) : null}
        </div>
        <div className="flex items-center gap-2">
          <Button
            variant="primary"
            size="sm"
            loading={runMut.isPending}
            onClick={() => runMut.mutate()}
            disabled={!c.isActive || c.steps.length === 0}
            title={c.isActive ? "Run now" : "Activate the chain to run it"}
          >
            <Play className="size-4" />
            Run now
          </Button>
          <Link
            href={`/chains/${chainId}/edit` as never}
            className="inline-flex h-8 items-center gap-1.5 rounded-md border border-[color:var(--color-border)] bg-[color:var(--color-surface)] px-3 text-sm hover:bg-[color:var(--color-surface-2)] transition-colors"
          >
            <Pencil className="size-3.5" /> Edit
          </Link>
        </div>
      </div>

      {note ? <p className="mb-4 text-sm text-[color:var(--color-novarad-red)]">{note}</p> : null}

      <section className="mb-8">
        <h2 className="mb-2 text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)]">
          Steps
        </h2>
        <ol className="space-y-1.5">
          {c.steps.map((s) => (
            <li
              key={s.stepOrder}
              className="flex flex-wrap items-center gap-x-3 gap-y-1 rounded-md border border-[color:var(--color-border)] bg-[color:var(--color-surface)] px-3 py-2"
            >
              <span className="w-6 text-center font-mono text-xs text-[color:var(--color-accent)]">
                {s.stepOrder}
              </span>
              <Link
                href={`/scripts/${s.scriptId}` as never}
                className="min-w-0 flex-1 truncate text-sm font-medium hover:text-[color:var(--color-accent)] transition-colors"
              >
                {s.scriptName}
              </Link>
              <LanguageBadge language={s.language} />
              {s.continueOnFailure ? <Badge variant="neutral">okay to fail</Badge> : null}
              {s.scriptIsActive ? null : <Badge variant="caution">script inactive</Badge>}
            </li>
          ))}
        </ol>
      </section>

      <section>
        <h2 className="mb-2 text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)]">
          Runs
        </h2>
        {runs.isLoading ? (
          <Spinner size={20} />
        ) : (runs.data ?? []).length === 0 ? (
          <p className="rounded-md border border-dashed border-[color:var(--color-border)] px-4 py-8 text-center text-sm text-[color:var(--color-muted-fg)]">
            Never run. Press Run now to watch the first one live.
          </p>
        ) : (
          <ul className="space-y-2">
            {(runs.data ?? []).map((r) => (
              <RunRow
                key={r.chainRunId}
                run={r}
                chain={c}
                expanded={expandedRunId === r.chainRunId}
                onToggle={() =>
                  setExpandedRunId((cur) => (cur === r.chainRunId ? null : r.chainRunId))
                }
                onNote={setNote}
              />
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}

function RunRow({
  run: r,
  chain,
  expanded,
  onToggle,
  onNote,
}: {
  run: ChainRunInfo;
  chain: ChainDetail;
  expanded: boolean;
  onToggle: () => void;
  onNote: (note: string) => void;
}) {
  const qc = useQueryClient();
  const live = isLive(r.status);

  const detail = useQuery({
    queryKey: ["chain-run", r.chainRunId],
    queryFn: () => chainsApi.runDetail(r.chainRunId),
    enabled: expanded,
    refetchInterval: (q) => (q.state.data && isLive(q.state.data.run.status) ? 2_000 : false),
  });

  const cancelMut = useMutation({
    mutationFn: () => chainsApi.cancelRun(r.chainRunId),
    onSuccess: (res) => {
      onNote(res.message);
      void qc.invalidateQueries({ queryKey: ["chain-runs", r.chainId] });
      void qc.invalidateQueries({ queryKey: ["chain-run", r.chainRunId] });
    },
    onError: () => onNote("Couldn't cancel the run. Try again."),
  });

  const run = detail.data?.run ?? r;
  const steps = detail.data?.steps ?? [];
  // While live, show the not-yet-started plan rows greyed out under the
  // executions that already exist — the console fills in as steps land.
  const queued = isLive(run.status) ? chain.steps.slice(steps.length) : [];
  const skipped =
    !isLive(run.status) && run.status !== "success" && steps.length < run.stepsTotal
      ? run.stepsTotal - steps.length
      : 0;

  return (
    <li className="rounded-lg border border-[color:var(--color-border)] bg-[color:var(--color-surface)]">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2 px-4 py-2.5">
        <span className="inline-flex items-center gap-0.5 font-mono text-xs text-[color:var(--color-muted-fg)]">
          #{run.chainRunId}
          <CopyId value={String(run.chainRunId)} label={`run ${run.chainRunId} ID`} />
        </span>
        <RunStatus status={run.status} />
        <span className="text-xs text-[color:var(--color-muted-fg)]">
          {run.triggeredBy} · {formatDateTime(run.startedAt ?? run.createdAt)}
          {run.durationMs != null ? ` · ${formatDuration(run.durationMs)}` : ""}
        </span>
        <span className="min-w-0 flex-1 truncate text-xs text-[color:var(--color-muted-fg)]">
          {run.stepsSucceeded} ok / {run.stepsFailed} failed of {run.stepsTotal}
        </span>
        <div className="flex items-center gap-1.5">
          {live ? (
            <Button
              variant="ghost"
              size="sm"
              loading={cancelMut.isPending}
              onClick={() => cancelMut.mutate()}
              title="Stop the current step and skip the rest"
            >
              <Square className="size-3.5" />
              Cancel
            </Button>
          ) : null}
          <button
            type="button"
            onClick={onToggle}
            aria-expanded={expanded}
            aria-label={expanded ? "Hide run detail" : "Show run detail"}
            className="inline-flex items-center rounded-md p-1.5 text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-accent)] hover:bg-[color:var(--color-surface-2)] transition-colors"
          >
            <ChevronDown
              className={`size-4 transition-transform duration-200 ${expanded ? "rotate-180" : ""}`}
            />
          </button>
        </div>
      </div>

      <AnimatePresence initial={false}>
        {expanded ? (
          <motion.div
            initial={{ height: 0, opacity: 0 }}
            animate={{ height: "auto", opacity: 1 }}
            exit={{ height: 0, opacity: 0 }}
            transition={{ duration: 0.18 }}
            className="overflow-hidden"
          >
            <div className="border-t border-[color:var(--color-border)] px-4 py-3 space-y-2">
              {detail.isLoading ? (
                <Spinner size={16} />
              ) : (
                <>
                  <ol className="space-y-1">
                    {steps.map((s, i) => (
                      <li
                        key={s.executionId}
                        className="flex flex-wrap items-center gap-x-3 gap-y-1 rounded bg-[color:var(--color-surface-2)]/60 px-3 py-1.5 text-sm"
                      >
                        <span className="w-6 text-center font-mono text-xs text-[color:var(--color-accent)]">
                          {i + 1}
                        </span>
                        <Link
                          href={`/scripts/${s.scriptId}` as never}
                          className="min-w-0 flex-1 truncate hover:text-[color:var(--color-accent)] transition-colors"
                          title="Open the script (execution logs live on its page)"
                        >
                          {s.scriptName}
                        </Link>
                        <RunStatus status={s.status} />
                        <span className="text-xs text-[color:var(--color-muted-fg)]">
                          {s.durationMs != null ? formatDuration(s.durationMs) : ""}
                          {s.rowsAffected != null ? ` · ${s.rowsAffected} rows` : ""}
                        </span>
                      </li>
                    ))}
                    {queued.map((s, i) => (
                      <li
                        key={`queued-${s.stepOrder}`}
                        className="flex flex-wrap items-center gap-x-3 gap-y-1 rounded px-3 py-1.5 text-sm opacity-50"
                      >
                        <span className="w-6 text-center font-mono text-xs">
                          {steps.length + i + 1}
                        </span>
                        <span className="min-w-0 flex-1 truncate">{s.scriptName}</span>
                        <span className="text-xs text-[color:var(--color-muted-fg)]">queued</span>
                      </li>
                    ))}
                  </ol>
                  {skipped > 0 ? (
                    <p className="text-xs text-[color:var(--color-caution)]">
                      {skipped} remaining step{skipped === 1 ? "" : "s"} skipped.
                    </p>
                  ) : null}
                  {run.errorSummary ? (
                    <div className="rounded-md border border-[color:var(--color-novarad-red)]/40 bg-[color:var(--color-novarad-red)]/10 px-3 py-2">
                      <p className="text-xs font-medium text-[color:var(--color-novarad-red)]">
                        Error summary
                      </p>
                      <pre className="mt-0.5 whitespace-pre-wrap text-xs font-mono">
                        {run.errorSummary}
                      </pre>
                    </div>
                  ) : null}
                </>
              )}
            </div>
          </motion.div>
        ) : null}
      </AnimatePresence>
    </li>
  );
}
