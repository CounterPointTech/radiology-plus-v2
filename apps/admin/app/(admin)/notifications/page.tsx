"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AnimatePresence, motion } from "framer-motion";
import { BellRing, ChevronDown, PenLine, RotateCcw, XCircle } from "lucide-react";
import Link from "next/link";
import { useState } from "react";

import {
  ChannelBadge,
  CopyId,
  inputCls,
  MessagePreview,
  QueueStatus,
} from "@/components/notifications/notification-bits";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Spinner } from "@/components/ui/spinner";
import { notificationsApi } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import type {
  NotificationChannelToken,
  NotificationQueueItem,
  NotificationStats,
  NotificationStatusToken,
} from "@/lib/types";
import { CHANNEL_LABEL, NOTIFICATION_CHANNELS, NOTIFICATION_STATUSES } from "@/lib/types";
import { formatDateTime } from "@/lib/utils";

const PAGE_SIZE = 50;

export default function NotificationsQueuePage() {
  const { user, isHydrated } = useAuth();
  const [status, setStatus] = useState<NotificationStatusToken | "">("");
  const [channel, setChannel] = useState<NotificationChannelToken | "">("");
  const [offset, setOffset] = useState(0);
  const [expandedId, setExpandedId] = useState<number | null>(null);
  const [actionNote, setActionNote] = useState<string | null>(null);

  const stats = useQuery({
    queryKey: ["notif-stats"],
    queryFn: () => notificationsApi.stats(),
    enabled: !!user,
    // Tighten the poll while anything is in flight so the dashboard feels live.
    refetchInterval: (q) =>
      (q.state.data?.pending ?? 0) + (q.state.data?.sending ?? 0) > 0 ? 4_000 : 30_000,
  });

  const busyQueue = (stats.data?.pending ?? 0) + (stats.data?.sending ?? 0) > 0;
  const queue = useQuery({
    queryKey: ["notif-queue", status, channel, offset],
    queryFn: () =>
      notificationsApi.queue({
        status: status || undefined,
        channel: channel || undefined,
        limit: PAGE_SIZE,
        offset,
      }),
    enabled: !!user,
    refetchInterval: busyQueue ? 4_000 : 30_000,
  });

  if (!isHydrated || !user) {
    return (
      <div className="min-h-[60vh] flex items-center justify-center">
        <Spinner size={28} />
      </div>
    );
  }

  const rows = queue.data?.items ?? [];
  const total = queue.data?.total ?? 0;

  return (
    <div className="space-y-6">
      <StatTiles stats={stats.data} />

      <div className="flex flex-wrap items-center gap-2">
        <select
          value={status}
          onChange={(e) => {
            setStatus(e.target.value as NotificationStatusToken | "");
            setOffset(0);
          }}
          aria-label="Filter by status"
          className={`${inputCls} w-auto`}
        >
          <option value="">All statuses</option>
          {NOTIFICATION_STATUSES.map((s) => (
            <option key={s} value={s}>
              {s}
            </option>
          ))}
        </select>
        <select
          value={channel}
          onChange={(e) => {
            setChannel(e.target.value as NotificationChannelToken | "");
            setOffset(0);
          }}
          aria-label="Filter by channel"
          className={`${inputCls} w-auto`}
        >
          <option value="">All channels</option>
          {NOTIFICATION_CHANNELS.map((c) => (
            <option key={c} value={c}>
              {CHANNEL_LABEL[c]}
            </option>
          ))}
        </select>
        <span className="text-xs text-[color:var(--color-muted-fg)]">
          {total} message{total === 1 ? "" : "s"}
        </span>
        <div className="ml-auto">
          <Link
            href="/notifications/compose"
            className="inline-flex h-8 items-center gap-2 rounded-md bg-[color:var(--color-accent)] px-3 text-sm font-medium text-[color:var(--color-accent-fg)] hover:brightness-110 transition"
          >
            <PenLine className="size-4" />
            Compose
          </Link>
        </div>
      </div>

      {actionNote ? (
        <p className="text-sm text-[color:var(--color-accent)]">{actionNote}</p>
      ) : null}

      {queue.isLoading ? (
        <div className="min-h-[30vh] flex items-center justify-center">
          <Spinner size={24} />
        </div>
      ) : queue.isError ? (
        <p className="text-sm text-[color:var(--color-novarad-red)]">
          Couldn&apos;t load the queue.{" "}
          <button className="underline underline-offset-2" onClick={() => queue.refetch()}>
            Try again
          </button>
        </p>
      ) : rows.length === 0 ? (
        <div className="rounded-lg border border-dashed border-[color:var(--color-border)] px-6 py-16 text-center rise-in">
          <BellRing className="size-8 mx-auto text-[color:var(--color-accent)]" />
          <p className="mt-3 text-sm text-[color:var(--color-muted-fg)]">
            {status || channel
              ? "Nothing matches these filters."
              : "The queue is empty. Compose a message or wait for the system to send one."}
          </p>
        </div>
      ) : (
        <ul className="space-y-2">
          {rows.map((n) => (
            <QueueRow
              key={n.notificationId}
              item={n}
              expanded={expandedId === n.notificationId}
              onToggle={() =>
                setExpandedId((cur) => (cur === n.notificationId ? null : n.notificationId))
              }
              onAction={(note) => setActionNote(note)}
            />
          ))}
        </ul>
      )}

      {total > PAGE_SIZE ? (
        <div className="flex items-center justify-between text-sm">
          <Button
            variant="secondary"
            size="sm"
            disabled={offset === 0}
            onClick={() => setOffset(Math.max(0, offset - PAGE_SIZE))}
          >
            Newer
          </Button>
          <span className="text-xs text-[color:var(--color-muted-fg)]">
            {offset + 1}–{Math.min(offset + PAGE_SIZE, total)} of {total}
          </span>
          <Button
            variant="secondary"
            size="sm"
            disabled={offset + PAGE_SIZE >= total}
            onClick={() => setOffset(offset + PAGE_SIZE)}
          >
            Older
          </Button>
        </div>
      ) : null}
    </div>
  );
}

function StatTiles({ stats }: { stats: NotificationStats | undefined }) {
  const tiles: { label: string; value: number | null; tone: string; hint?: string | null }[] = [
    {
      label: "Pending",
      value: stats?.pending ?? null,
      tone: "text-[color:var(--color-base-fg)]",
      hint: stats?.oldestPendingAt ? `oldest ${formatDateTime(stats.oldestPendingAt)}` : null,
    },
    {
      label: "Sending",
      value: stats?.sending ?? null,
      tone: "text-[color:var(--color-accent)]",
    },
    {
      label: "Sent (24h)",
      value: stats?.sent24h ?? null,
      tone: "text-[color:var(--color-success)]",
    },
    {
      label: "Failed",
      value: stats?.failed ?? null,
      tone: "text-[color:var(--color-novarad-red)]",
    },
  ];

  return (
    <div>
      <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
        {tiles.map((t) => (
          <div
            key={t.label}
            className="rounded-lg border border-[color:var(--color-border)] bg-[color:var(--color-surface)] px-4 py-3"
          >
            <p className="text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)]">
              {t.label}
            </p>
            <p className={`mt-1 text-2xl tabular-nums ${t.tone}`} style={{ fontFamily: "var(--font-display)" }}>
              {t.value ?? "—"}
            </p>
            <p className="mt-0.5 min-h-4 text-[11px] text-[color:var(--color-muted-fg)]">
              {t.hint ?? ""}
            </p>
          </div>
        ))}
      </div>
      {stats && stats.byChannel24h.length > 0 ? (
        <p className="mt-2 text-xs text-[color:var(--color-muted-fg)]">
          Last 24h by channel:{" "}
          {stats.byChannel24h
            .map((c) => `${CHANNEL_LABEL[c.channel] ?? c.channel} ${c.count}`)
            .join(" · ")}
        </p>
      ) : null}
    </div>
  );
}

function QueueRow({
  item: n,
  expanded,
  onToggle,
  onAction,
}: {
  item: NotificationQueueItem;
  expanded: boolean;
  onToggle: () => void;
  onAction: (note: string) => void;
}) {
  const qc = useQueryClient();

  const detail = useQuery({
    queryKey: ["notif-item", n.notificationId],
    queryFn: () => notificationsApi.queueItem(n.notificationId),
    enabled: expanded,
  });

  const invalidate = () => {
    void qc.invalidateQueries({ queryKey: ["notif-queue"] });
    void qc.invalidateQueries({ queryKey: ["notif-stats"] });
    void qc.invalidateQueries({ queryKey: ["notif-item", n.notificationId] });
  };
  const cancelMut = useMutation({
    mutationFn: () => notificationsApi.cancel(n.notificationId),
    onSuccess: (r) => {
      onAction(r.message);
      invalidate();
    },
    onError: () => onAction("Couldn't cancel the message. Try again."),
  });
  const retryMut = useMutation({
    mutationFn: () => notificationsApi.retry(n.notificationId),
    onSuccess: (r) => {
      onAction(r.message);
      invalidate();
    },
    onError: () => onAction("Couldn't requeue the message. Try again."),
  });

  const canCancel = n.status === "pending";
  const canRetry = n.status === "failed" || n.status === "cancelled";

  return (
    <li className="rounded-lg border border-[color:var(--color-border)] bg-[color:var(--color-surface)] transition-[border-color,box-shadow] hover:border-[color:var(--color-accent)]/40">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2 px-4 py-2.5">
        <span className="inline-flex items-center gap-0.5 text-xs font-mono text-[color:var(--color-muted-fg)]">
          #{n.notificationId}
          <CopyId value={String(n.notificationId)} label={`notification ${n.notificationId} ID`} />
        </span>
        <ChannelBadge channel={n.channel} />
        <span className="min-w-0 flex-1 truncate text-sm">
          <span className="font-medium">{n.recipient}</span>
          {n.subject ? (
            <span className="text-[color:var(--color-muted-fg)]"> — {n.subject}</span>
          ) : null}
        </span>
        {n.templateName ? <Badge variant="neutral">{n.templateName}</Badge> : null}
        {n.retryCount > 0 ? (
          <Badge variant="caution">retry {n.retryCount}/{n.maxRetries}</Badge>
        ) : null}
        <QueueStatus status={n.status} />
        <span className="text-xs text-[color:var(--color-muted-fg)]">
          {formatDateTime(n.sentAt ?? n.failedAt ?? n.createdAt)}
        </span>

        <div className="flex items-center gap-1.5">
          {canCancel ? (
            <Button
              variant="ghost"
              size="sm"
              loading={cancelMut.isPending}
              onClick={() => cancelMut.mutate()}
              title="Cancel before it sends"
            >
              <XCircle className="size-3.5" />
              Cancel
            </Button>
          ) : null}
          {canRetry ? (
            <Button
              variant="secondary"
              size="sm"
              loading={retryMut.isPending}
              onClick={() => retryMut.mutate()}
              title="Queue a fresh attempt"
            >
              <RotateCcw className="size-3.5" />
              Retry
            </Button>
          ) : null}
          <button
            type="button"
            onClick={onToggle}
            aria-expanded={expanded}
            aria-label={expanded ? "Hide details" : "Show details"}
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
            <div className="border-t border-[color:var(--color-border)] px-4 py-3 space-y-3">
              {detail.isLoading ? (
                <Spinner size={16} />
              ) : detail.isError || !detail.data ? (
                <p className="text-sm text-[color:var(--color-novarad-red)]">
                  Couldn&apos;t load the message detail.
                </p>
              ) : (
                <>
                  <div className="grid gap-x-6 gap-y-1 text-xs text-[color:var(--color-muted-fg)] sm:grid-cols-2">
                    <span>Created {formatDateTime(detail.data.createdAt)}</span>
                    <span>Scheduled {formatDateTime(detail.data.scheduledAt)}</span>
                    {detail.data.sentAt ? <span>Sent {formatDateTime(detail.data.sentAt)}</span> : null}
                    {detail.data.failedAt ? (
                      <span>Failed {formatDateTime(detail.data.failedAt)}</span>
                    ) : null}
                    <span>Priority {detail.data.priority}</span>
                    {detail.data.sourceType ? (
                      <span>Source {detail.data.sourceType}</span>
                    ) : null}
                  </div>
                  {detail.data.lastError ? (
                    <div className="rounded-md border border-[color:var(--color-novarad-red)]/40 bg-[color:var(--color-novarad-red)]/10 px-3 py-2">
                      <p className="text-xs font-medium text-[color:var(--color-novarad-red)]">
                        Last error
                      </p>
                      <p className="mt-0.5 whitespace-pre-wrap text-xs">{detail.data.lastError}</p>
                    </div>
                  ) : null}
                  <MessagePreview body={detail.data.body} isHtml={detail.data.isHtml} />
                </>
              )}
            </div>
          </motion.div>
        ) : null}
      </AnimatePresence>
    </li>
  );
}
