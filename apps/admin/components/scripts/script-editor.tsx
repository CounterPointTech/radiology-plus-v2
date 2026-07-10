"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AnimatePresence, motion } from "framer-motion";
import { History, RotateCcw, Save, X } from "lucide-react";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { AxiosError } from "axios";

import { CodeEditor } from "@/components/scripts/code-editor";
import { Button } from "@/components/ui/button";
import { Spinner } from "@/components/ui/spinner";
import { scriptsApi } from "@/lib/api";
import { CRON_PRESETS, describeCron } from "@/lib/cron";
import type {
  ConnectionTargetToken,
  ScriptDetail,
  ScriptLanguageToken,
  ScriptSaveRequest,
} from "@/lib/types";
import { LANGUAGE_LABEL, TARGET_LABEL, TARGETS_FOR_LANGUAGE } from "@/lib/types";
import { formatDateTime } from "@/lib/utils";

const LANGUAGES: ScriptLanguageToken[] = ["pgsql", "tsql", "powershell", "batch"];

const DEFAULT_BODY: Record<ScriptLanguageToken, string> = {
  pgsql: "-- PostgreSQL\nSELECT 1;\n",
  tsql: "-- SQL Server (M*Modal)\nSELECT 1;\n",
  powershell: "# PowerShell 7\nWrite-Output 'hello'\n",
  batch: ":: Windows batch\n@echo off\necho hello\n",
};

export interface EditorInitial {
  name: string;
  description: string;
  language: ScriptLanguageToken;
  body: string;
  connectionTarget: ConnectionTargetToken;
  cronExpression: string;
  isActive: boolean;
  timeoutSeconds: number;
}

export function initialFromDetail(d: ScriptDetail, asCopy = false): EditorInitial {
  return {
    name: asCopy ? `${d.name} (copy)` : d.name,
    description: d.description ?? "",
    language: d.language,
    body: d.body,
    connectionTarget: d.connectionTarget,
    cronExpression: d.cronExpression ?? "",
    isActive: d.isActive,
    timeoutSeconds: d.timeoutSeconds,
  };
}

export const EMPTY_INITIAL: EditorInitial = {
  name: "",
  description: "",
  language: "pgsql",
  body: DEFAULT_BODY.pgsql,
  connectionTarget: "appdb",
  cronExpression: "",
  isActive: true,
  timeoutSeconds: 300,
};

/**
 * Create/edit form. In edit mode, saving a changed body snapshots the old one
 * server-side; the History panel lists those versions and can restore one into
 * the editor (restoring only changes the form — Save commits it).
 */
export function ScriptEditor({
  scriptId,
  initial,
}: {
  scriptId?: string; // undefined = create mode
  initial: EditorInitial;
}) {
  const router = useRouter();
  const qc = useQueryClient();

  const [form, setForm] = useState<EditorInitial>(initial);
  const [error, setError] = useState<string | null>(null);
  const [showHistory, setShowHistory] = useState(false);

  const isEdit = scriptId !== undefined;
  const targets = TARGETS_FOR_LANGUAGE[form.language];
  const cronHint = form.cronExpression.trim() ? describeCron(form.cronExpression) : null;

  function patch(p: Partial<EditorInitial>) {
    setForm((f) => ({ ...f, ...p }));
  }

  function switchLanguage(language: ScriptLanguageToken) {
    const allowed = TARGETS_FOR_LANGUAGE[language];
    const bodyUntouched =
      form.body === DEFAULT_BODY[form.language] || form.body.trim() === "";
    patch({
      language,
      connectionTarget: allowed.includes(form.connectionTarget)
        ? form.connectionTarget
        : allowed[0]!,
      ...(bodyUntouched && !isEdit ? { body: DEFAULT_BODY[language] } : {}),
    });
  }

  const save = useMutation({
    mutationFn: async () => {
      const body: ScriptSaveRequest = {
        name: form.name.trim(),
        description: form.description.trim() ? form.description.trim() : null,
        language: form.language,
        body: form.body,
        connectionTarget: form.connectionTarget,
        cronExpression: form.cronExpression.trim() ? form.cronExpression.trim() : null,
        isActive: form.isActive,
        timeoutSeconds: form.timeoutSeconds,
      };
      return isEdit ? scriptsApi.update(scriptId, body) : scriptsApi.create(body);
    },
    onSuccess: (saved) => {
      qc.invalidateQueries({ queryKey: ["scripts"] });
      qc.invalidateQueries({ queryKey: ["script", saved.scriptId] });
      qc.invalidateQueries({ queryKey: ["script-versions", saved.scriptId] });
      router.push(`/scripts/${saved.scriptId}` as never);
    },
    onError: (err) => {
      const ax = err as AxiosError<{ error?: string }>;
      setError(ax.response?.data?.error ?? "Couldn't save the script. Try again.");
    },
  });

  function submit() {
    if (!form.name.trim()) {
      setError("Give the script a name.");
      return;
    }
    if (!form.body.trim()) {
      setError("The script body is empty.");
      return;
    }
    setError(null);
    save.mutate();
  }

  return (
    <div className="space-y-5">
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="Name" required>
          <input
            value={form.name}
            onChange={(e) => patch({ name: e.target.value })}
            placeholder="e.g. Reset stuck worklist rows"
            className={inputCls}
          />
        </Field>
        <Field label="Description">
          <input
            value={form.description}
            onChange={(e) => patch({ description: e.target.value })}
            placeholder="What does this script do?"
            className={inputCls}
          />
        </Field>
        <Field label="Language" required>
          <select
            value={form.language}
            onChange={(e) => switchLanguage(e.target.value as ScriptLanguageToken)}
            className={inputCls}
          >
            {LANGUAGES.map((l) => (
              <option key={l} value={l}>
                {LANGUAGE_LABEL[l]}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Runs against" required>
          <select
            value={form.connectionTarget}
            onChange={(e) => patch({ connectionTarget: e.target.value as ConnectionTargetToken })}
            disabled={targets.length === 1}
            className={inputCls}
          >
            {targets.map((t) => (
              <option key={t} value={t}>
                {TARGET_LABEL[t]}
              </option>
            ))}
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
        <div className="grid grid-cols-2 gap-4">
          <Field label="Timeout (seconds)">
            <input
              type="number"
              min={1}
              max={86_400}
              value={form.timeoutSeconds}
              onChange={(e) => patch({ timeoutSeconds: Number(e.target.value) || 300 })}
              className={inputCls}
            />
          </Field>
          <Field label="Status">
            <label className="inline-flex h-9 items-center gap-2 text-sm cursor-pointer select-none">
              <input
                type="checkbox"
                checked={form.isActive}
                onChange={(e) => patch({ isActive: e.target.checked })}
                className="size-4 accent-[color:var(--color-accent)]"
              />
              Active
            </label>
          </Field>
        </div>
      </div>

      <div>
        <div className="mb-1.5 flex items-center justify-between">
          <span className="text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)]">
            Script body
          </span>
          {isEdit ? (
            <button
              type="button"
              onClick={() => setShowHistory((v) => !v)}
              className="inline-flex items-center gap-1.5 text-xs text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-accent)] transition-colors"
            >
              <History className="size-3.5" />
              {showHistory ? "Hide history" : "Version history"}
            </button>
          ) : null}
        </div>
        <CodeEditor
          value={form.body}
          onChange={(v) => patch({ body: v })}
          language={form.language}
          minHeight="20rem"
          maxHeight="42rem"
        />
      </div>

      <AnimatePresence>
        {isEdit && showHistory ? (
          <VersionHistory
            scriptId={scriptId}
            language={form.language}
            onRestore={(body) => {
              patch({ body });
              setShowHistory(false);
            }}
          />
        ) : null}
      </AnimatePresence>

      {error ? <p className="text-sm text-[color:var(--color-novarad-red)]">{error}</p> : null}

      <div className="flex items-center gap-3">
        <Button loading={save.isPending} onClick={submit}>
          <Save className="size-4" />
          {isEdit ? "Save changes" : "Create script"}
        </Button>
        <Button variant="ghost" onClick={() => router.back()}>
          Cancel
        </Button>
        {isEdit ? (
          <span className="text-xs text-[color:var(--color-muted-fg)]">
            Changing the body keeps the previous version in history.
          </span>
        ) : null}
      </div>
    </div>
  );
}

function VersionHistory({
  scriptId,
  language,
  onRestore,
}: {
  scriptId: string;
  language: ScriptLanguageToken;
  onRestore: (body: string) => void;
}) {
  const [openVersionId, setOpenVersionId] = useState<string | null>(null);

  const versions = useQuery({
    queryKey: ["script-versions", scriptId],
    queryFn: () => scriptsApi.versions(scriptId),
  });
  const openVersion = useQuery({
    queryKey: ["script-version", openVersionId],
    queryFn: () => scriptsApi.version(openVersionId!),
    enabled: openVersionId !== null,
  });

  return (
    <motion.div
      initial={{ opacity: 0, height: 0 }}
      animate={{ opacity: 1, height: "auto" }}
      exit={{ opacity: 0, height: 0 }}
      transition={{ duration: 0.25, ease: "easeOut" }}
      className="overflow-hidden"
    >
      <div className="rounded-lg border border-[color:var(--color-border)] bg-[color:var(--color-surface)] p-4 space-y-3">
        <h3 className="text-sm font-medium flex items-center gap-2">
          <History className="size-4 text-[color:var(--color-accent)]" />
          Version history
        </h3>
        {versions.isLoading ? (
          <Spinner size={16} />
        ) : (versions.data ?? []).length === 0 ? (
          <p className="text-sm text-[color:var(--color-muted-fg)]">
            No prior versions — history starts the first time you save a body change.
          </p>
        ) : (
          <ul className="divide-y divide-[color:var(--color-border)]/60">
            {(versions.data ?? []).map((v) => (
              <li key={v.versionId} className="py-2">
                <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-sm">
                  <span className="font-mono text-[color:var(--color-accent)]">
                    v{v.versionNumber}
                  </span>
                  <span className="text-xs text-[color:var(--color-muted-fg)]">
                    {formatDateTime(v.savedAt)} · {v.bodyChars.toLocaleString()} chars
                  </span>
                  <span className="ml-auto flex items-center gap-2">
                    <button
                      type="button"
                      onClick={() =>
                        setOpenVersionId(openVersionId === v.versionId ? null : v.versionId)
                      }
                      className="text-xs text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-accent)] transition-colors"
                    >
                      {openVersionId === v.versionId ? (
                        <span className="inline-flex items-center gap-1">
                          <X className="size-3.5" /> Close
                        </span>
                      ) : (
                        "View"
                      )}
                    </button>
                  </span>
                </div>
                <AnimatePresence>
                  {openVersionId === v.versionId ? (
                    <motion.div
                      initial={{ opacity: 0, height: 0 }}
                      animate={{ opacity: 1, height: "auto" }}
                      exit={{ opacity: 0, height: 0 }}
                      className="overflow-hidden"
                    >
                      <div className="mt-2 space-y-2">
                        {openVersion.isLoading ? (
                          <Spinner size={14} />
                        ) : openVersion.data ? (
                          <>
                            <CodeEditor
                              value={openVersion.data.body}
                              language={language}
                              readOnly
                              minHeight="6rem"
                              maxHeight="18rem"
                            />
                            <Button
                              variant="secondary"
                              size="sm"
                              onClick={() => onRestore(openVersion.data!.body)}
                            >
                              <RotateCcw className="size-3.5" />
                              Restore into editor
                            </Button>
                          </>
                        ) : null}
                      </div>
                    </motion.div>
                  ) : null}
                </AnimatePresence>
              </li>
            ))}
          </ul>
        )}
      </div>
    </motion.div>
  );
}

function Field({
  label,
  required,
  children,
}: {
  label: string;
  required?: boolean;
  children: React.ReactNode;
}) {
  return (
    <label className="flex flex-col gap-1.5 text-sm">
      <span className="text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)]">
        {label}
        {required ? <span className="text-[color:var(--color-accent)]"> *</span> : null}
      </span>
      {children}
    </label>
  );
}

const inputCls =
  "h-9 w-full rounded-md border border-[color:var(--color-border)] bg-[color:var(--color-surface)] px-2.5 text-sm " +
  "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-accent)]/60 disabled:opacity-60";
