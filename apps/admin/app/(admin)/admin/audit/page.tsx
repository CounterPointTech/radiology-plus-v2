"use client";

import { useQuery } from "@tanstack/react-query";
import { AnimatePresence, motion } from "framer-motion";
import { ChevronDown, ScrollText } from "lucide-react";
import { useState } from "react";

import { CopyId, inputCls } from "@/components/notifications/notification-bits";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Spinner } from "@/components/ui/spinner";
import { auditApi } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import type { AuditActionToken, AuditLogItem } from "@/lib/types";
import { AUDIT_ACTIONS } from "@/lib/types";
import { formatDateTime } from "@/lib/utils";

const PAGE_SIZE = 50;

/** Local calendar date -> UTC instant at local midnight (exclusive end adds a day). */
function dayStart(date: string): string {
  return new Date(`${date}T00:00:00`).toISOString();
}
function dayEnd(date: string): string {
  const d = new Date(`${date}T00:00:00`);
  d.setDate(d.getDate() + 1);
  return d.toISOString();
}

export default function AuditPage() {
  const { user: me, isHydrated } = useAuth();
  const [username, setUsername] = useState("");
  const [action, setAction] = useState<AuditActionToken | "">("");
  const [success, setSuccess] = useState<"" | "true" | "false">("");
  const [fromDate, setFromDate] = useState("");
  const [toDate, setToDate] = useState("");
  const [offset, setOffset] = useState(0);
  const [expandedId, setExpandedId] = useState<number | null>(null);

  const page = useQuery({
    queryKey: ["audit", username, action, success, fromDate, toDate, offset],
    queryFn: () =>
      auditApi.list({
        username: username.trim() || undefined,
        action: action || undefined,
        success: success === "" ? undefined : success === "true",
        from: fromDate ? dayStart(fromDate) : undefined,
        to: toDate ? dayEnd(toDate) : undefined,
        limit: PAGE_SIZE,
        offset,
      }),
    enabled: !!me,
  });

  if (!isHydrated || !me) {
    return (
      <div className="min-h-[60vh] flex items-center justify-center">
        <Spinner size={28} />
      </div>
    );
  }

  const rows = page.data?.items ?? [];
  const total = page.data?.total ?? 0;

  function resetPaging() {
    setOffset(0);
    setExpandedId(null);
  }

  return (
    <div className="mx-auto max-w-6xl px-6 py-8">
      <div className="mb-6 rise-in">
        <p className="text-[10px] uppercase tracking-[0.3em] text-[color:var(--color-accent)]">
          Admin
        </p>
        <h1 className="text-3xl mt-1" style={{ fontFamily: "var(--font-display)" }}>
          Audit Log<span className="caret-blink text-[color:var(--color-accent)]">▍</span>
        </h1>
        <p className="text-sm text-[color:var(--color-muted-fg)] mt-1 max-w-2xl">
          Every sign-in, change, and run the system has recorded — who did what, when, and
          from where.
        </p>
      </div>

      <div className="mb-4 flex flex-wrap items-center gap-2">
        <input
          value={username}
          onChange={(e) => {
            setUsername(e.target.value);
            resetPaging();
          }}
          placeholder="Filter by user…"
          aria-label="Filter by username"
          className={`${inputCls} w-48`}
        />
        <select
          value={action}
          onChange={(e) => {
            setAction(e.target.value as AuditActionToken | "");
            resetPaging();
          }}
          aria-label="Filter by action"
          className={`${inputCls} w-auto`}
        >
          <option value="">All actions</option>
          {AUDIT_ACTIONS.map((a) => (
            <option key={a} value={a}>
              {a}
            </option>
          ))}
        </select>
        <select
          value={success}
          onChange={(e) => {
            setSuccess(e.target.value as "" | "true" | "false");
            resetPaging();
          }}
          aria-label="Filter by outcome"
          className={`${inputCls} w-auto`}
        >
          <option value="">All outcomes</option>
          <option value="true">Success</option>
          <option value="false">Failure</option>
        </select>
        <input
          type="date"
          value={fromDate}
          onChange={(e) => {
            setFromDate(e.target.value);
            resetPaging();
          }}
          aria-label="From date"
          className={`${inputCls} w-auto`}
        />
        <span className="text-xs text-[color:var(--color-muted-fg)]">to</span>
        <input
          type="date"
          value={toDate}
          onChange={(e) => {
            setToDate(e.target.value);
            resetPaging();
          }}
          aria-label="To date"
          className={`${inputCls} w-auto`}
        />
        <span className="text-xs text-[color:var(--color-muted-fg)]">
          {total} entr{total === 1 ? "y" : "ies"}
        </span>
      </div>

      {page.isLoading ? (
        <div className="min-h-[30vh] flex items-center justify-center">
          <Spinner size={24} />
        </div>
      ) : page.isError ? (
        <p className="text-sm text-[color:var(--color-novarad-red)]">
          Couldn&apos;t load the audit log.{" "}
          <button className="underline underline-offset-2" onClick={() => page.refetch()}>
            Try again
          </button>
        </p>
      ) : rows.length === 0 ? (
        <div className="rounded-lg border border-dashed border-[color:var(--color-border)] px-6 py-16 text-center rise-in">
          <ScrollText className="size-8 mx-auto text-[color:var(--color-accent)]" />
          <p className="mt-3 text-sm text-[color:var(--color-muted-fg)]">
            Nothing matches these filters.
          </p>
        </div>
      ) : (
        <ul className="space-y-1.5">
          {rows.map((r) => (
            <AuditRow
              key={r.logId}
              row={r}
              expanded={expandedId === r.logId}
              onToggle={() => setExpandedId((cur) => (cur === r.logId ? null : r.logId))}
            />
          ))}
        </ul>
      )}

      {total > PAGE_SIZE ? (
        <div className="mt-4 flex items-center justify-between text-sm">
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

/** The writer stores the human description in metadata JSON — surface it as the summary. */
function describe(r: AuditLogItem): string {
  if (r.metadataJson) {
    try {
      const meta = JSON.parse(r.metadataJson) as { description?: string };
      if (meta.description) return meta.description;
    } catch {
      // fall through to the other columns
    }
  }
  return r.errorMessage ?? r.resourceId ?? r.resourceType;
}

function AuditRow({
  row: r,
  expanded,
  onToggle,
}: {
  row: AuditLogItem;
  expanded: boolean;
  onToggle: () => void;
}) {
  return (
    <li className="rounded-lg border border-[color:var(--color-border)] bg-[color:var(--color-surface)]">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1.5 px-4 py-2">
        <span className="inline-flex items-center gap-0.5 font-mono text-xs text-[color:var(--color-muted-fg)]">
          #{r.logId}
          <CopyId value={String(r.logId)} label={`log entry ${r.logId} ID`} />
        </span>
        <span className="w-40 truncate text-sm font-medium">{r.username ?? "—"}</span>
        <Badge variant={r.success ? "accent" : "danger"}>{r.action}</Badge>
        <span className="min-w-0 flex-1 truncate text-xs text-[color:var(--color-muted-fg)]">
          {describe(r)}
        </span>
        <span className="text-xs text-[color:var(--color-muted-fg)]">
          {formatDateTime(r.occurredAt)}
        </span>
        <button
          type="button"
          onClick={onToggle}
          aria-expanded={expanded}
          aria-label={expanded ? "Hide detail" : "Show detail"}
          className="inline-flex items-center rounded-md p-1.5 text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-accent)] hover:bg-[color:var(--color-surface-2)] transition-colors"
        >
          <ChevronDown
            className={`size-4 transition-transform duration-200 ${expanded ? "rotate-180" : ""}`}
          />
        </button>
      </div>

      <AnimatePresence initial={false}>
        {expanded ? (
          <motion.div
            initial={{ height: 0, opacity: 0 }}
            animate={{ height: "auto", opacity: 1 }}
            exit={{ height: 0, opacity: 0 }}
            transition={{ duration: 0.15 }}
            className="overflow-hidden"
          >
            <div className="space-y-2 border-t border-[color:var(--color-border)] px-4 py-3 text-xs">
              <div className="grid gap-x-6 gap-y-1 text-[color:var(--color-muted-fg)] sm:grid-cols-2">
                <span>Resource: {r.resourceType}</span>
                {r.ipAddress ? <span>IP: {r.ipAddress}</span> : null}
                {r.userAgent ? <span className="sm:col-span-2 truncate">Agent: {r.userAgent}</span> : null}
              </div>
              <pre className="whitespace-pre-wrap rounded-md bg-[color:var(--color-surface-2)] px-3 py-2 font-mono">
                {describe(r)}
              </pre>
              {r.errorMessage ? (
                <p className="whitespace-pre-wrap text-[color:var(--color-novarad-red)]">
                  {r.errorMessage}
                </p>
              ) : null}
              {r.metadataJson ? (
                <pre className="max-h-40 overflow-auto whitespace-pre-wrap rounded-md bg-[color:var(--color-surface-2)] px-3 py-2 font-mono">
                  {r.metadataJson}
                </pre>
              ) : null}
            </div>
          </motion.div>
        ) : null}
      </AnimatePresence>
    </li>
  );
}
