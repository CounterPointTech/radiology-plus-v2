"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AxiosError } from "axios";
import { Reorder, useDragControls } from "framer-motion";
import { ChevronDown, ChevronUp, GripVertical, Plus, Save, Trash2 } from "lucide-react";
import { useRouter } from "next/navigation";
import { useState } from "react";

import { Field, inputCls } from "@/components/notifications/notification-bits";
import { LanguageBadge } from "@/components/scripts/script-bits";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { chainsApi, notificationsApi, scriptsApi } from "@/lib/api";
import { CRON_PRESETS, describeCron } from "@/lib/cron";
import type { ChainDetail, ChainOnFailure, ChainSaveRequest, ScriptSummary } from "@/lib/types";

interface StepRow {
  uid: number; // stable row identity — the same script may appear twice
  scriptId: string;
  continueOnFailure: boolean;
}

export interface ChainFormState {
  name: string;
  description: string;
  onFailure: ChainOnFailure;
  cronExpression: string;
  isActive: boolean;
  notifyOnFailureRecipient: string;
  notifyOnFailureTemplateId: string;
  steps: StepRow[];
}

let nextUid = 1;
function uid(): number {
  return nextUid++;
}

export const EMPTY_CHAIN: ChainFormState = {
  name: "",
  description: "",
  onFailure: "stop",
  cronExpression: "",
  isActive: true,
  notifyOnFailureRecipient: "",
  notifyOnFailureTemplateId: "",
  steps: [],
};

export function chainFormFromDetail(d: ChainDetail): ChainFormState {
  return {
    name: d.name,
    description: d.description ?? "",
    onFailure: d.onFailure,
    cronExpression: d.cronExpression ?? "",
    isActive: d.isActive,
    notifyOnFailureRecipient: d.notifyOnFailureRecipient ?? "",
    notifyOnFailureTemplateId: d.notifyOnFailureTemplateId ?? "",
    steps: d.steps.map((s) => ({
      uid: uid(),
      scriptId: s.scriptId,
      continueOnFailure: s.continueOnFailure,
    })),
  };
}

/**
 * Create/edit form for a chain: pick scripts, order them (drag the grip, or use
 * the arrow buttons), set the failure policy, schedule, and the optional
 * failure-notification email.
 */
export function ChainEditor({
  chainId,
  initial,
}: {
  chainId?: string; // undefined = create mode
  initial: ChainFormState;
}) {
  const router = useRouter();
  const qc = useQueryClient();

  const [form, setForm] = useState<ChainFormState>(initial);
  const [pickerScriptId, setPickerScriptId] = useState("");
  const [error, setError] = useState<string | null>(null);

  const isEdit = chainId !== undefined;
  const cronHint = form.cronExpression.trim() ? describeCron(form.cronExpression) : null;

  const scripts = useQuery({ queryKey: ["scripts"], queryFn: () => scriptsApi.list() });
  const templates = useQuery({
    queryKey: ["notif-templates"],
    queryFn: () => notificationsApi.templates(),
  });
  const emailTemplates = (templates.data ?? []).filter(
    (t) => t.channel === "email" && t.isActive,
  );
  const scriptById = new Map((scripts.data ?? []).map((s) => [s.scriptId, s]));

  function patch(p: Partial<ChainFormState>) {
    setForm((f) => ({ ...f, ...p }));
  }

  function addStep() {
    if (!pickerScriptId) return;
    patch({
      steps: [...form.steps, { uid: uid(), scriptId: pickerScriptId, continueOnFailure: false }],
    });
    setPickerScriptId("");
  }

  function patchStep(rowUid: number, p: Partial<StepRow>) {
    patch({ steps: form.steps.map((s) => (s.uid === rowUid ? { ...s, ...p } : s)) });
  }

  function removeStep(rowUid: number) {
    patch({ steps: form.steps.filter((s) => s.uid !== rowUid) });
  }

  function moveStep(index: number, delta: -1 | 1) {
    const target = index + delta;
    if (target < 0 || target >= form.steps.length) return;
    const next = [...form.steps];
    [next[index], next[target]] = [next[target]!, next[index]!];
    patch({ steps: next });
  }

  const save = useMutation({
    mutationFn: async () => {
      const body: ChainSaveRequest = {
        name: form.name.trim(),
        description: form.description.trim() ? form.description.trim() : null,
        onFailure: form.onFailure,
        cronExpression: form.cronExpression.trim() ? form.cronExpression.trim() : null,
        isActive: form.isActive,
        notifyOnFailureRecipient: form.notifyOnFailureRecipient.trim()
          ? form.notifyOnFailureRecipient.trim()
          : null,
        notifyOnFailureTemplateId: form.notifyOnFailureTemplateId || null,
        steps: form.steps.map((s) => ({
          scriptId: s.scriptId,
          continueOnFailure: s.continueOnFailure,
        })),
      };
      return isEdit ? chainsApi.update(chainId, body) : chainsApi.create(body);
    },
    onSuccess: (saved) => {
      void qc.invalidateQueries({ queryKey: ["chains"] });
      void qc.invalidateQueries({ queryKey: ["chain", saved.chainId] });
      router.push(`/chains/${saved.chainId}` as never);
    },
    onError: (err) => {
      const ax = err as AxiosError<{ error?: string }>;
      setError(ax.response?.data?.error ?? "Couldn't save the chain. Try again.");
    },
  });

  function submit() {
    if (!form.name.trim()) {
      setError("Give the chain a name.");
      return;
    }
    if (form.steps.length === 0) {
      setError("Add at least one step.");
      return;
    }
    if (form.notifyOnFailureTemplateId && !form.notifyOnFailureRecipient.trim()) {
      setError("A failure-notification template needs a recipient.");
      return;
    }
    setError(null);
    save.mutate();
  }

  return (
    <div className="space-y-6">
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="Name" required>
          <input
            value={form.name}
            onChange={(e) => patch({ name: e.target.value })}
            placeholder="e.g. Nightly cleanup"
            className={inputCls}
          />
        </Field>
        <Field label="Description">
          <input
            value={form.description}
            onChange={(e) => patch({ description: e.target.value })}
            placeholder="What does this chain do?"
            className={inputCls}
          />
        </Field>
        <Field
          label="If a step fails"
          hint={
            form.onFailure === "stop"
              ? "Remaining steps are skipped and the chain fails (steps marked okay-to-fail are tolerated)."
              : "Every step still runs; the chain fails if any non-okay-to-fail step failed."
          }
        >
          <select
            value={form.onFailure}
            onChange={(e) => patch({ onFailure: e.target.value as ChainOnFailure })}
            className={inputCls}
          >
            <option value="stop">Stop the chain</option>
            <option value="continue">Keep going</option>
          </select>
        </Field>
        <Field label="Schedule (cron, UTC — blank = on demand)">
          <div className="flex gap-2">
            <input
              value={form.cronExpression}
              onChange={(e) => patch({ cronExpression: e.target.value })}
              placeholder="0 2 * * *"
              className={`${inputCls} font-mono flex-1`}
            />
            <select
              value=""
              onChange={(e) => {
                if (e.target.value) patch({ cronExpression: e.target.value });
              }}
              aria-label="Cron presets"
              className={`${inputCls} w-auto`}
            >
              <option value="">Presets…</option>
              {CRON_PRESETS.map((p) => (
                <option key={p.expression} value={p.expression}>
                  {p.label}
                </option>
              ))}
            </select>
          </div>
          <p className="mt-1 text-xs text-[color:var(--color-muted-fg)] min-h-4">
            {form.cronExpression.trim()
              ? (cronHint ?? "Custom schedule — the exact next run shows after saving.")
              : "No schedule — runs only when you press Run."}
          </p>
        </Field>
      </div>

      <div>
        <p className="mb-2 text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)]">
          Steps (run in order)
        </p>

        {form.steps.length === 0 ? (
          <p className="rounded-md border border-dashed border-[color:var(--color-border)] px-4 py-6 text-center text-sm text-[color:var(--color-muted-fg)]">
            No steps yet — pick a script below to add the first one.
          </p>
        ) : (
          <Reorder.Group
            axis="y"
            values={form.steps}
            onReorder={(steps: StepRow[]) => patch({ steps })}
            className="space-y-1.5"
          >
            {form.steps.map((step, i) => (
              <StepRowItem
                key={step.uid}
                step={step}
                index={i}
                total={form.steps.length}
                script={scriptById.get(step.scriptId)}
                onToggleOkayToFail={(v) => patchStep(step.uid, { continueOnFailure: v })}
                onMove={(d) => moveStep(i, d)}
                onRemove={() => removeStep(step.uid)}
              />
            ))}
          </Reorder.Group>
        )}

        <div className="mt-3 flex items-center gap-2">
          <select
            value={pickerScriptId}
            onChange={(e) => setPickerScriptId(e.target.value)}
            aria-label="Script to add"
            className={`${inputCls} w-auto min-w-64`}
          >
            <option value="">
              {scripts.isLoading ? "Loading scripts…" : "Pick a script to add…"}
            </option>
            {(scripts.data ?? []).map((s) => (
              <option key={s.scriptId} value={s.scriptId}>
                {s.name}
                {s.isActive ? "" : " (inactive)"}
              </option>
            ))}
          </select>
          <Button variant="secondary" size="sm" onClick={addStep} disabled={!pickerScriptId}>
            <Plus className="size-4" />
            Add step
          </Button>
        </div>
        <p className="mt-1.5 text-xs text-[color:var(--color-muted-fg)]">
          Drag the grip (or use the arrows) to reorder. The same script can appear more than
          once. An inactive script fails its step when the chain runs.
        </p>
      </div>

      <div className="grid gap-4 sm:grid-cols-2">
        <Field
          label="On failure, email"
          hint="Blank = no notification. Sent through the notifications queue."
        >
          <input
            value={form.notifyOnFailureRecipient}
            onChange={(e) => patch({ notifyOnFailureRecipient: e.target.value })}
            placeholder="name@example.com"
            className={inputCls}
          />
        </Field>
        <Field
          label="Notification template"
          hint="Optional — a built-in message is used otherwise. Variables: chainName, chainRunId, stepsTotal, stepsSucceeded, stepsFailed, errorSummary."
        >
          <select
            value={form.notifyOnFailureTemplateId}
            onChange={(e) => patch({ notifyOnFailureTemplateId: e.target.value })}
            disabled={!form.notifyOnFailureRecipient.trim()}
            className={inputCls}
          >
            <option value="">Built-in message</option>
            {emailTemplates.map((t) => (
              <option key={t.templateId} value={t.templateId}>
                {t.name}
              </option>
            ))}
          </select>
        </Field>
      </div>

      <label className="inline-flex items-center gap-2 text-sm cursor-pointer select-none">
        <input
          type="checkbox"
          checked={form.isActive}
          onChange={(e) => patch({ isActive: e.target.checked })}
          className="size-4 accent-[color:var(--color-accent)]"
        />
        Active
      </label>

      {error ? <p className="text-sm text-[color:var(--color-novarad-red)]">{error}</p> : null}

      <div className="flex items-center gap-2">
        <Button loading={save.isPending} onClick={submit}>
          <Save className="size-4" />
          {isEdit ? "Save chain" : "Create chain"}
        </Button>
        <Button variant="ghost" onClick={() => router.push("/chains" as never)}>
          Cancel
        </Button>
      </div>
    </div>
  );
}

function StepRowItem({
  step,
  index,
  total,
  script,
  onToggleOkayToFail,
  onMove,
  onRemove,
}: {
  step: StepRow;
  index: number;
  total: number;
  script: ScriptSummary | undefined;
  onToggleOkayToFail: (v: boolean) => void;
  onMove: (delta: -1 | 1) => void;
  onRemove: () => void;
}) {
  const controls = useDragControls();

  return (
    <Reorder.Item
      value={step}
      dragListener={false}
      dragControls={controls}
      className="rounded-md border border-[color:var(--color-border)] bg-[color:var(--color-surface)]"
    >
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2 px-3 py-2">
        <button
          type="button"
          onPointerDown={(e) => controls.start(e)}
          title="Drag to reorder"
          aria-label={`Drag step ${index + 1}`}
          className="cursor-grab touch-none text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-accent)]"
        >
          <GripVertical className="size-4" />
        </button>
        <span className="w-6 text-center font-mono text-xs text-[color:var(--color-accent)]">
          {index + 1}
        </span>
        <span className="min-w-0 flex-1 truncate text-sm font-medium">
          {script?.name ?? step.scriptId}
        </span>
        {script ? <LanguageBadge language={script.language} /> : null}
        {script && !script.isActive ? <Badge variant="caution">script inactive</Badge> : null}
        <label className="inline-flex items-center gap-1.5 text-xs cursor-pointer select-none text-[color:var(--color-muted-fg)]">
          <input
            type="checkbox"
            checked={step.continueOnFailure}
            onChange={(e) => onToggleOkayToFail(e.target.checked)}
            className="size-3.5 accent-[color:var(--color-accent)]"
          />
          okay to fail
        </label>
        <div className="flex items-center gap-0.5">
          <button
            type="button"
            onClick={() => onMove(-1)}
            disabled={index === 0}
            title="Move up"
            aria-label={`Move step ${index + 1} up`}
            className="inline-flex items-center rounded p-1 text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-accent)] disabled:opacity-30"
          >
            <ChevronUp className="size-4" />
          </button>
          <button
            type="button"
            onClick={() => onMove(1)}
            disabled={index === total - 1}
            title="Move down"
            aria-label={`Move step ${index + 1} down`}
            className="inline-flex items-center rounded p-1 text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-accent)] disabled:opacity-30"
          >
            <ChevronDown className="size-4" />
          </button>
          <button
            type="button"
            onClick={onRemove}
            title="Remove step"
            aria-label={`Remove step ${index + 1}`}
            className="inline-flex items-center rounded p-1 text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-novarad-red)]"
          >
            <Trash2 className="size-3.5" />
          </button>
        </div>
      </div>
    </Reorder.Item>
  );
}
