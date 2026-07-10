"use client";

import { Check, Copy } from "lucide-react";
import { useState } from "react";

import { Badge } from "@/components/ui/badge";
import type { NotificationChannelToken, NotificationStatusToken } from "@/lib/types";
import { CHANNEL_LABEL } from "@/lib/types";

export function ChannelBadge({ channel }: { channel: NotificationChannelToken }) {
  return (
    <Badge variant="accent" className="font-mono">
      {CHANNEL_LABEL[channel] ?? channel}
    </Badge>
  );
}

/** Colored dot + label for a queue status; the sending state pulses. */
export function QueueStatus({ status }: { status: NotificationStatusToken }) {
  const dot: Record<NotificationStatusToken, string> = {
    pending: "bg-[color:var(--color-muted-fg)]",
    sending: "bg-[color:var(--color-accent)] pulse-dot",
    sent: "bg-[color:var(--color-success)]",
    failed: "bg-[color:var(--color-novarad-red)]",
    cancelled: "bg-[color:var(--color-caution)]",
  };
  const text: Record<NotificationStatusToken, string> = {
    pending: "text-[color:var(--color-muted-fg)]",
    sending: "text-[color:var(--color-accent)]",
    sent: "text-[color:var(--color-success)]",
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

/** Explicit copy affordance for IDs — clicking text never triggers anything. */
export function CopyId({ value, label }: { value: string; label: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <button
      type="button"
      title={`Copy ${label}`}
      aria-label={`Copy ${label}`}
      onClick={() => {
        void navigator.clipboard.writeText(value).then(() => {
          setCopied(true);
          window.setTimeout(() => setCopied(false), 1_200);
        });
      }}
      className="inline-flex items-center rounded p-1 text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-accent)] hover:bg-[color:var(--color-surface-2)] transition-colors"
    >
      {copied ? (
        <Check className="size-3.5 text-[color:var(--color-success)]" />
      ) : (
        <Copy className="size-3.5" />
      )}
    </button>
  );
}

export function Field({
  label,
  required,
  hint,
  children,
}: {
  label: string;
  required?: boolean;
  hint?: string | null;
  children: React.ReactNode;
}) {
  return (
    <label className="flex flex-col gap-1.5 text-sm">
      <span className="text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)]">
        {label}
        {required ? <span className="text-[color:var(--color-accent)]"> *</span> : null}
      </span>
      {children}
      {hint ? (
        <span className="text-xs text-[color:var(--color-muted-fg)]">{hint}</span>
      ) : null}
    </label>
  );
}

export const inputCls =
  "h-9 w-full rounded-md border border-[color:var(--color-border)] bg-[color:var(--color-surface)] px-2.5 text-sm " +
  "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-accent)]/60 disabled:opacity-60";

export const textareaCls =
  "w-full rounded-md border border-[color:var(--color-border)] bg-[color:var(--color-surface)] px-2.5 py-2 text-sm " +
  "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-accent)]/60 disabled:opacity-60 resize-y";

/** Rendered-message preview: HTML in a sandboxed iframe, text in a pre. */
export function MessagePreview({ body, isHtml }: { body: string; isHtml: boolean }) {
  if (isHtml) {
    return (
      <iframe
        title="Message preview"
        sandbox=""
        srcDoc={body}
        className="h-48 w-full rounded-md border border-[color:var(--color-border)] bg-white"
      />
    );
  }
  return (
    <pre className="max-h-48 overflow-auto whitespace-pre-wrap rounded-md border border-[color:var(--color-border)] bg-[color:var(--color-surface-2)] px-3 py-2 text-xs font-mono">
      {body}
    </pre>
  );
}
