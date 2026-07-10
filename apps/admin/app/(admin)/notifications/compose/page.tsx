"use client";

import { useMutation, useQuery } from "@tanstack/react-query";
import { AxiosError } from "axios";
import { Eye, Send } from "lucide-react";
import Link from "next/link";
import { useEffect, useState } from "react";

import {
  Field,
  inputCls,
  MessagePreview,
  QueueStatus,
  textareaCls,
} from "@/components/notifications/notification-bits";
import type { SampleVariable } from "@/components/notifications/template-editor";
import { VariableRows, variablesToObject } from "@/components/notifications/template-editor";
import { Button } from "@/components/ui/button";
import { Spinner } from "@/components/ui/spinner";
import { notificationsApi } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import type {
  NotificationChannelToken,
  NotificationQueueDetail,
  TemplatePreviewResult,
} from "@/lib/types";
import { CHANNEL_LABEL, LIVE_CHANNELS, NOTIFICATION_CHANNELS } from "@/lib/types";
import { formatDateTime } from "@/lib/utils";

type ComposeMode = "adhoc" | "template";

export default function ComposePage() {
  const { user, isHydrated } = useAuth();

  const [mode, setMode] = useState<ComposeMode>("adhoc");
  const [recipient, setRecipient] = useState("");
  const [priority, setPriority] = useState(5);
  const [error, setError] = useState<string | null>(null);

  // Ad-hoc fields
  const [channel, setChannel] = useState<NotificationChannelToken>("email");
  const [subject, setSubject] = useState("");
  const [body, setBody] = useState("");
  const [isHtml, setIsHtml] = useState(false);

  // Template fields
  const [templateId, setTemplateId] = useState("");
  const [variables, setVariables] = useState<SampleVariable[]>([]);
  const [preview, setPreview] = useState<TemplatePreviewResult | null>(null);
  const [previewError, setPreviewError] = useState<string | null>(null);

  const [queued, setQueued] = useState<NotificationQueueDetail | null>(null);

  const templates = useQuery({
    queryKey: ["notif-templates"],
    queryFn: () => notificationsApi.templates(),
    enabled: !!user && mode === "template",
  });
  const activeTemplates = (templates.data ?? []).filter((t) => t.isActive);

  const selectedTemplate = useQuery({
    queryKey: ["notif-template", templateId],
    queryFn: () => notificationsApi.template(templateId),
    enabled: !!user && mode === "template" && !!templateId,
  });

  // Live preview of the selected template with the entered variables.
  useEffect(() => {
    if (mode !== "template" || !selectedTemplate.data) {
      setPreview(null);
      setPreviewError(null);
      return;
    }
    const detail = selectedTemplate.data;
    const vars = variablesToObject(variables);
    const handle = window.setTimeout(() => {
      notificationsApi
        .previewTemplate({
          subjectTemplate: detail.subjectTemplate,
          bodyTemplate: detail.bodyTemplate,
          variables: vars,
        })
        .then((r) => {
          setPreview(r);
          setPreviewError(null);
        })
        .catch((err: AxiosError<{ error?: string }>) => {
          setPreviewError(err.response?.data?.error ?? "Preview failed.");
        });
    }, 400);
    return () => window.clearTimeout(handle);
  }, [mode, selectedTemplate.data, variables]);

  const composeMut = useMutation({
    mutationFn: () =>
      notificationsApi.compose(
        mode === "template"
          ? {
              recipient: recipient.trim(),
              isHtml: false, // template drives isHtml server-side
              priority,
              templateId,
              variables: variablesToObject(variables),
            }
          : {
              channel,
              recipient: recipient.trim(),
              subject: subject.trim() ? subject.trim() : null,
              body,
              isHtml,
              priority,
            },
      ),
    onSuccess: (item) => {
      setQueued(item);
      setError(null);
    },
    onError: (err) => {
      const ax = err as AxiosError<{ error?: string }>;
      setError(ax.response?.data?.error ?? "Couldn't queue the message. Try again.");
    },
  });

  // Watch the queued message move pending → sending → sent (or failed).
  const watched = useQuery({
    queryKey: ["notif-item", queued?.notificationId],
    queryFn: () => notificationsApi.queueItem(queued!.notificationId),
    enabled: !!queued,
    refetchInterval: (q) => {
      const s = q.state.data?.status;
      return s === "pending" || s === "sending" ? 2_000 : false;
    },
  });
  const watchedItem = watched.data ?? queued;

  if (!isHydrated || !user) {
    return (
      <div className="min-h-[60vh] flex items-center justify-center">
        <Spinner size={28} />
      </div>
    );
  }

  function submit() {
    if (!recipient.trim()) {
      setError("Who is this going to? Enter a recipient.");
      return;
    }
    if (mode === "adhoc" && !body.trim()) {
      setError("The message body is empty.");
      return;
    }
    if (mode === "template" && !templateId) {
      setError("Pick a template to send from.");
      return;
    }
    setError(null);
    composeMut.mutate();
  }

  return (
    <div className="grid gap-6 xl:grid-cols-2">
      <div className="space-y-4">
        <div className="inline-flex rounded-md border border-[color:var(--color-border)] p-0.5">
          {(
            [
              { key: "adhoc", label: "Write it here" },
              { key: "template", label: "From a template" },
            ] as { key: ComposeMode; label: string }[]
          ).map((m) => (
            <button
              key={m.key}
              type="button"
              onClick={() => setMode(m.key)}
              className={`rounded px-3 py-1.5 text-sm transition-colors ${
                mode === m.key
                  ? "bg-[color:var(--color-accent)]/15 text-[color:var(--color-accent)]"
                  : "text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-base-fg)]"
              }`}
            >
              {m.label}
            </button>
          ))}
        </div>

        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Recipient" required>
            <input
              value={recipient}
              onChange={(e) => setRecipient(e.target.value)}
              placeholder="name@example.com"
              className={inputCls}
            />
          </Field>
          <Field label="Priority" hint="1 sends first, 10 last. 5 is normal.">
            <input
              type="number"
              min={1}
              max={10}
              value={priority}
              onChange={(e) =>
                setPriority(Math.min(10, Math.max(1, Number(e.target.value) || 5)))
              }
              className={inputCls}
            />
          </Field>
        </div>

        {mode === "adhoc" ? (
          <>
            <Field
              label="Channel"
              required
              hint={
                LIVE_CHANNELS.includes(channel)
                  ? null
                  : `${CHANNEL_LABEL[channel]} messages queue, but no sender is configured yet — they won't deliver.`
              }
            >
              <select
                value={channel}
                onChange={(e) => setChannel(e.target.value as NotificationChannelToken)}
                className={inputCls}
              >
                {NOTIFICATION_CHANNELS.map((c) => (
                  <option key={c} value={c}>
                    {CHANNEL_LABEL[c]}
                  </option>
                ))}
              </select>
            </Field>
            <Field label="Subject">
              <input
                value={subject}
                onChange={(e) => setSubject(e.target.value)}
                placeholder="Optional"
                className={inputCls}
              />
            </Field>
            <Field label="Body" required>
              <textarea
                value={body}
                onChange={(e) => setBody(e.target.value)}
                rows={10}
                className={`${textareaCls} ${isHtml ? "font-mono text-xs" : "text-sm"}`}
              />
            </Field>
            <label className="inline-flex items-center gap-2 text-sm cursor-pointer select-none">
              <input
                type="checkbox"
                checked={isHtml}
                onChange={(e) => setIsHtml(e.target.checked)}
                className="size-4 accent-[color:var(--color-accent)]"
              />
              HTML body
            </label>
          </>
        ) : (
          <>
            <Field label="Template" required>
              <select
                value={templateId}
                onChange={(e) => setTemplateId(e.target.value)}
                className={inputCls}
              >
                <option value="">
                  {templates.isLoading ? "Loading…" : "Pick a template…"}
                </option>
                {activeTemplates.map((t) => (
                  <option key={t.templateId} value={t.templateId}>
                    {t.name} ({CHANNEL_LABEL[t.channel]})
                  </option>
                ))}
              </select>
            </Field>
            {templates.data && activeTemplates.length === 0 ? (
              <p className="text-xs text-[color:var(--color-muted-fg)]">
                No active templates.{" "}
                <Link
                  href="/notifications/templates/new"
                  className="text-[color:var(--color-accent)] underline underline-offset-2"
                >
                  Create one
                </Link>{" "}
                first.
              </p>
            ) : null}
            {templateId ? (
              <div>
                <p className="mb-1.5 text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)]">
                  Variables
                </p>
                <VariableRows variables={variables} onChange={setVariables} />
              </div>
            ) : null}
          </>
        )}

        {error ? <p className="text-sm text-[color:var(--color-novarad-red)]">{error}</p> : null}

        <Button loading={composeMut.isPending} onClick={submit}>
          <Send className="size-4" />
          Queue message
        </Button>
      </div>

      <div className="space-y-4">
        {mode === "template" && templateId ? (
          <div>
            <p className="mb-1.5 inline-flex items-center gap-1.5 text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)]">
              <Eye className="size-3.5" />
              Preview
            </p>
            {previewError ? (
              <p className="rounded-md border border-[color:var(--color-caution)]/40 bg-[color:var(--color-caution)]/10 px-3 py-2 text-xs text-[color:var(--color-caution)]">
                {previewError}
              </p>
            ) : preview ? (
              <div className="space-y-2">
                {preview.subject !== null ? (
                  <p className="rounded-md border border-[color:var(--color-border)] bg-[color:var(--color-surface)] px-3 py-2 text-sm">
                    <span className="text-xs text-[color:var(--color-muted-fg)]">Subject: </span>
                    {preview.subject}
                  </p>
                ) : null}
                <MessagePreview
                  body={preview.body}
                  isHtml={selectedTemplate.data?.isHtml ?? false}
                />
              </div>
            ) : (
              <Spinner size={16} />
            )}
          </div>
        ) : null}

        {watchedItem ? (
          <div className="rounded-lg border border-[color:var(--color-accent)]/40 bg-[color:var(--color-accent)]/5 px-4 py-3 space-y-2 rise-in">
            <div className="flex flex-wrap items-center gap-3">
              <span className="text-sm font-medium">
                Message #{watchedItem.notificationId} queued
              </span>
              <QueueStatus status={watchedItem.status} />
              <span className="text-xs text-[color:var(--color-muted-fg)]">
                {formatDateTime(watchedItem.sentAt ?? watchedItem.createdAt)}
              </span>
            </div>
            {watchedItem.status === "pending" || watchedItem.status === "sending" ? (
              <p className="text-xs text-[color:var(--color-muted-fg)]">
                The background service picks pending messages up within ~15 seconds.
              </p>
            ) : null}
            {watchedItem.lastError ? (
              <p className="whitespace-pre-wrap text-xs text-[color:var(--color-novarad-red)]">
                {watchedItem.lastError}
              </p>
            ) : null}
            <Link
              href="/notifications"
              className="inline-block text-xs text-[color:var(--color-accent)] underline underline-offset-2"
            >
              View it in the queue
            </Link>
          </div>
        ) : null}
      </div>
    </div>
  );
}
