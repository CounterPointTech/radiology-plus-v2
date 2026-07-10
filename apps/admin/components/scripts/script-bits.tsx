"use client";

import { Badge } from "@/components/ui/badge";
import type { ConnectionTargetToken, ScriptLanguageToken, ScriptRunStatus } from "@/lib/types";
import { LANGUAGE_LABEL, TARGET_LABEL } from "@/lib/types";

export function LanguageBadge({ language }: { language: ScriptLanguageToken }) {
  return (
    <Badge variant="accent" className="font-mono">
      {LANGUAGE_LABEL[language] ?? language}
    </Badge>
  );
}

export function TargetBadge({ target }: { target: ConnectionTargetToken }) {
  if (target === "none") return null;
  return <Badge variant="neutral">{TARGET_LABEL[target] ?? target}</Badge>;
}

/** Colored dot + label for a run status; the running state pulses. */
export function RunStatus({ status }: { status: ScriptRunStatus | null }) {
  if (!status) {
    return <span className="text-xs text-[color:var(--color-muted-fg)]">never run</span>;
  }
  const dot: Record<ScriptRunStatus, string> = {
    pending: "bg-[color:var(--color-muted-fg)]",
    running: "bg-[color:var(--color-accent)] pulse-dot",
    success: "bg-[color:var(--color-success)]",
    failed: "bg-[color:var(--color-novarad-red)]",
    cancelled: "bg-[color:var(--color-caution)]",
  };
  const text: Record<ScriptRunStatus, string> = {
    pending: "text-[color:var(--color-muted-fg)]",
    running: "text-[color:var(--color-accent)]",
    success: "text-[color:var(--color-success)]",
    failed: "text-[color:var(--color-novarad-red)]",
    cancelled: "text-[color:var(--color-caution)]",
  };
  return (
    <span className={`inline-flex items-center gap-1.5 text-xs font-medium ${text[status]}`}>
      <span className={`inline-block size-2 rounded-full ${dot[status]}`} />
      {status}
    </span>
  );
}

export function formatDuration(ms: number | null | undefined): string {
  if (ms == null) return "—";
  if (ms < 1_000) return `${ms}ms`;
  if (ms < 60_000) return `${(ms / 1_000).toFixed(1)}s`;
  return `${Math.floor(ms / 60_000)}m ${Math.round((ms % 60_000) / 1_000)}s`;
}
