"use client";

import { useMutation, useQueryClient } from "@tanstack/react-query";
import { AxiosError } from "axios";
import { Eye, Plus, Save, Trash2 } from "lucide-react";
import { useRouter } from "next/navigation";
import { useEffect, useMemo, useState } from "react";

import {
  Field,
  inputCls,
  MessagePreview,
  textareaCls,
} from "@/components/notifications/notification-bits";
import { Button } from "@/components/ui/button";
import { notificationsApi } from "@/lib/api";
import type {
  NotificationChannelToken,
  NotificationTemplateDetail,
  NotificationTemplateSaveRequest,
  TemplatePreviewResult,
} from "@/lib/types";
import { CHANNEL_LABEL, LIVE_CHANNELS, NOTIFICATION_CHANNELS } from "@/lib/types";

export interface TemplateFormState {
  name: string;
  channel: NotificationChannelToken;
  subjectTemplate: string;
  bodyTemplate: string;
  isHtml: boolean;
  isActive: boolean;
}

export const EMPTY_TEMPLATE: TemplateFormState = {
  name: "",
  channel: "email",
  subjectTemplate: "",
  bodyTemplate: "Hello {{name}},\n\n",
  isHtml: false,
  isActive: true,
};

export function templateFormFromDetail(d: NotificationTemplateDetail): TemplateFormState {
  return {
    name: d.name,
    channel: d.channel,
    subjectTemplate: d.subjectTemplate ?? "",
    bodyTemplate: d.bodyTemplate,
    isHtml: d.isHtml,
    isActive: d.isActive,
  };
}

export interface SampleVariable {
  key: string;
  value: string;
}

export function variablesToObject(vars: SampleVariable[]): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const v of vars) {
    if (v.key.trim()) out[v.key.trim()] = v.value;
  }
  return out;
}

/** Editable key/value rows for Handlebars sample data. */
export function VariableRows({
  variables,
  onChange,
}: {
  variables: SampleVariable[];
  onChange: (next: SampleVariable[]) => void;
}) {
  return (
    <div className="space-y-1.5">
      {variables.map((v, i) => (
        // Index keys are fine here: rows are only appended/removed in place.
        // eslint-disable-next-line react/no-array-index-key
        <div key={i} className="flex items-center gap-1.5">
          <input
            value={v.key}
            onChange={(e) =>
              onChange(variables.map((x, j) => (j === i ? { ...x, key: e.target.value } : x)))
            }
            placeholder="variable"
            aria-label={`Variable ${i + 1} name`}
            className={`${inputCls} h-8 w-36 font-mono text-xs`}
          />
          <input
            value={v.value}
            onChange={(e) =>
              onChange(variables.map((x, j) => (j === i ? { ...x, value: e.target.value } : x)))
            }
            placeholder="sample value"
            aria-label={`Variable ${i + 1} value`}
            className={`${inputCls} h-8 flex-1 text-xs`}
          />
          <button
            type="button"
            onClick={() => onChange(variables.filter((_, j) => j !== i))}
            title="Remove variable"
            aria-label={`Remove variable ${v.key || i + 1}`}
            className="inline-flex items-center rounded-md p-1.5 text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-novarad-red)] hover:bg-[color:var(--color-surface-2)] transition-colors"
          >
            <Trash2 className="size-3.5" />
          </button>
        </div>
      ))}
      <button
        type="button"
        onClick={() => onChange([...variables, { key: "", value: "" }])}
        className="inline-flex items-center gap-1 rounded-md px-2 py-1 text-xs text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-accent)] hover:bg-[color:var(--color-surface-2)] transition-colors"
      >
        <Plus className="size-3.5" />
        Add variable
      </button>
    </div>
  );
}

/**
 * Create/edit form with a live server-rendered preview: the right panel renders
 * the actual Handlebars output (the same renderer the send path uses), refreshed
 * as you type, using the sample variables below it.
 */
export function TemplateEditor({
  templateId,
  initial,
}: {
  templateId?: string; // undefined = create mode
  initial: TemplateFormState;
}) {
  const router = useRouter();
  const qc = useQueryClient();

  const [form, setForm] = useState<TemplateFormState>(initial);
  const [variables, setVariables] = useState<SampleVariable[]>([
    { key: "name", value: "Dan" },
  ]);
  const [error, setError] = useState<string | null>(null);
  const [preview, setPreview] = useState<TemplatePreviewResult | null>(null);
  const [previewError, setPreviewError] = useState<string | null>(null);

  const isEdit = templateId !== undefined;

  function patch(p: Partial<TemplateFormState>) {
    setForm((f) => ({ ...f, ...p }));
  }

  // Debounced live preview — server-side render so what you see is what sends.
  const sampleJson = useMemo(() => JSON.stringify(variablesToObject(variables)), [variables]);
  useEffect(() => {
    if (!form.bodyTemplate.trim()) {
      setPreview(null);
      setPreviewError(null);
      return;
    }
    const handle = window.setTimeout(() => {
      notificationsApi
        .previewTemplate({
          subjectTemplate: form.subjectTemplate.trim() ? form.subjectTemplate : null,
          bodyTemplate: form.bodyTemplate,
          variables: JSON.parse(sampleJson) as Record<string, unknown>,
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
  }, [form.subjectTemplate, form.bodyTemplate, sampleJson]);

  const save = useMutation({
    mutationFn: async () => {
      const body: NotificationTemplateSaveRequest = {
        name: form.name.trim(),
        channel: form.channel,
        subjectTemplate: form.subjectTemplate.trim() ? form.subjectTemplate : null,
        bodyTemplate: form.bodyTemplate,
        isHtml: form.isHtml,
        isActive: form.isActive,
      };
      return isEdit
        ? notificationsApi.updateTemplate(templateId, body)
        : notificationsApi.createTemplate(body);
    },
    onSuccess: (saved) => {
      void qc.invalidateQueries({ queryKey: ["notif-templates"] });
      void qc.invalidateQueries({ queryKey: ["notif-template", saved.templateId] });
      router.push("/notifications/templates");
    },
    onError: (err) => {
      const ax = err as AxiosError<{ error?: string }>;
      setError(ax.response?.data?.error ?? "Couldn't save the template. Try again.");
    },
  });

  function submit() {
    if (!form.name.trim()) {
      setError("Give the template a name.");
      return;
    }
    if (!form.bodyTemplate.trim()) {
      setError("The message body is empty.");
      return;
    }
    setError(null);
    save.mutate();
  }

  return (
    <div className="grid gap-6 xl:grid-cols-2">
      <div className="space-y-4">
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Name" required>
            <input
              value={form.name}
              onChange={(e) => patch({ name: e.target.value })}
              placeholder="e.g. PACS outage alert"
              className={inputCls}
            />
          </Field>
          <Field
            label="Channel"
            required
            hint={
              LIVE_CHANNELS.includes(form.channel)
                ? null
                : `${CHANNEL_LABEL[form.channel]} messages queue, but no sender is configured yet — they won't deliver.`
            }
          >
            <select
              value={form.channel}
              onChange={(e) => patch({ channel: e.target.value as NotificationChannelToken })}
              className={inputCls}
            >
              {NOTIFICATION_CHANNELS.map((c) => (
                <option key={c} value={c}>
                  {CHANNEL_LABEL[c]}
                </option>
              ))}
            </select>
          </Field>
        </div>

        <Field label="Subject (Handlebars — blank for none)">
          <input
            value={form.subjectTemplate}
            onChange={(e) => patch({ subjectTemplate: e.target.value })}
            placeholder="e.g. {{modality}} worklist needs attention"
            className={`${inputCls} font-mono`}
          />
        </Field>

        <Field label="Body (Handlebars)" required>
          <textarea
            value={form.bodyTemplate}
            onChange={(e) => patch({ bodyTemplate: e.target.value })}
            rows={12}
            spellCheck={false}
            className={`${textareaCls} font-mono text-xs leading-relaxed`}
          />
        </Field>

        <div className="flex flex-wrap items-center gap-6">
          <label className="inline-flex items-center gap-2 text-sm cursor-pointer select-none">
            <input
              type="checkbox"
              checked={form.isHtml}
              onChange={(e) => patch({ isHtml: e.target.checked })}
              className="size-4 accent-[color:var(--color-accent)]"
            />
            HTML body
          </label>
          <label className="inline-flex items-center gap-2 text-sm cursor-pointer select-none">
            <input
              type="checkbox"
              checked={form.isActive}
              onChange={(e) => patch({ isActive: e.target.checked })}
              className="size-4 accent-[color:var(--color-accent)]"
            />
            Active
          </label>
        </div>

        {error ? <p className="text-sm text-[color:var(--color-novarad-red)]">{error}</p> : null}

        <div className="flex items-center gap-2">
          <Button loading={save.isPending} onClick={submit}>
            <Save className="size-4" />
            {isEdit ? "Save template" : "Create template"}
          </Button>
          <Button variant="ghost" onClick={() => router.push("/notifications/templates")}>
            Cancel
          </Button>
        </div>
      </div>

      <div className="space-y-4">
        <div>
          <p className="mb-1.5 inline-flex items-center gap-1.5 text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)]">
            <Eye className="size-3.5" />
            Live preview
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
              <MessagePreview body={preview.body} isHtml={form.isHtml} />
            </div>
          ) : (
            <p className="text-xs text-[color:var(--color-muted-fg)]">
              Start typing a body to see the rendered message.
            </p>
          )}
        </div>

        <div>
          <p className="mb-1.5 text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)]">
            Sample variables
          </p>
          <VariableRows variables={variables} onChange={setVariables} />
          <p className="mt-1.5 text-xs text-[color:var(--color-muted-fg)]">
            Reference them in the template as <code className="font-mono">{"{{variable}}"}</code>.
            These samples only drive the preview — real values come from whatever queues the
            message.
          </p>
        </div>
      </div>
    </div>
  );
}
