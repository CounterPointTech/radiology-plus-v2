"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AxiosError } from "axios";
import { Pencil, Plus, Trash2, X } from "lucide-react";
import { useState } from "react";

import { Button } from "@/components/ui/button";
import { Card, CardBody, CardHeader, CardSubtitle, CardTitle } from "@/components/ui/card";
import { Dialog, DialogActions } from "@/components/ui/dialog";
import { Input, Label, Textarea } from "@/components/ui/input";
import { Spinner } from "@/components/ui/spinner";
import { techApi } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { type TechNotesTemplate } from "@/lib/types";

interface FormState {
  label: string;
  body: string;
  sortOrder: string;
}

const EMPTY_FORM: FormState = { label: "", body: "", sortOrder: "" };

function errMessage(err: unknown, fallback: string): string {
  const ax = err as AxiosError<{ error?: string }>;
  return ax?.response?.data?.error ?? fallback;
}

export default function TemplatesPage() {
  const { user } = useAuth();
  const queryClient = useQueryClient();

  const templatesQuery = useQuery({
    queryKey: ["templates"],
    queryFn: () => techApi.templates(),
  });

  const [editingId, setEditingId] = useState<number | null>(null);
  const [form, setForm] = useState<FormState>(EMPTY_FORM);
  const [formError, setFormError] = useState<string | null>(null);
  const [confirmDelete, setConfirmDelete] = useState<TechNotesTemplate | null>(null);

  const isEditing = editingId !== null;

  function resetForm() {
    setEditingId(null);
    setForm(EMPTY_FORM);
    setFormError(null);
  }

  function startEdit(t: TechNotesTemplate) {
    setEditingId(t.templateId);
    setForm({ label: t.label, body: t.body, sortOrder: String(t.sortOrder) });
    setFormError(null);
  }

  const saveMutation = useMutation({
    mutationFn: async () => {
      const payload = {
        label: form.label.trim(),
        body: form.body.trim(),
        sortOrder: form.sortOrder.trim() ? Number(form.sortOrder) : 0,
      };
      return isEditing
        ? techApi.updateTemplate(editingId!, payload)
        : techApi.createTemplate(payload);
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["templates"] });
      resetForm();
    },
    onError: (err) =>
      setFormError(errMessage(err, "Couldn't save the template. Try again.")),
  });

  const deleteMutation = useMutation({
    mutationFn: (templateId: number) => techApi.deleteTemplate(templateId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["templates"] });
      setConfirmDelete(null);
    },
  });

  const templates = templatesQuery.data ?? [];
  const canSave = form.label.trim().length > 0 && form.body.trim().length > 0;

  return (
    <div className="mx-auto max-w-3xl px-6 py-8 space-y-6">
      <div>
        <p className="text-[10px] uppercase tracking-[0.3em] text-[color:var(--color-accent)]">
          Tech Validation
        </p>
        <h1 className="text-2xl mt-1" style={{ fontFamily: "var(--font-display)" }}>
          Reason templates
        </h1>
        <p className="text-sm text-[color:var(--color-muted-fg)] mt-1">
          Canned reasons that techs can one-tap into step 3 of the validation wizard.
          Shared across your facility.
        </p>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>{isEditing ? "Edit template" : "New template"}</CardTitle>
          <CardSubtitle>
            A short label for the chip, and the reason text it fills in.
          </CardSubtitle>
        </CardHeader>
        <CardBody className="space-y-4">
          <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
            <div className="space-y-1.5 sm:col-span-2">
              <Label htmlFor="label" required>
                Label
              </Label>
              <Input
                id="label"
                value={form.label}
                onChange={(e) => setForm({ ...form, label: e.target.value })}
                placeholder="e.g. Demographics confirmed"
                maxLength={60}
              />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="sort">Sort order</Label>
              <Input
                id="sort"
                type="number"
                value={form.sortOrder}
                onChange={(e) => setForm({ ...form, sortOrder: e.target.value })}
                placeholder="0"
              />
            </div>
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="body" required>
              Reason text
            </Label>
            <Textarea
              id="body"
              value={form.body}
              onChange={(e) => setForm({ ...form, body: e.target.value })}
              placeholder="Patient demographics confirmed against the order; no changes needed."
              rows={3}
              maxLength={500}
            />
          </div>
          {formError ? (
            <p className="text-sm text-[color:var(--color-novarad-red)]">{formError}</p>
          ) : null}
        </CardBody>
        <div className="px-5 py-3 border-t border-[color:var(--color-border)] flex items-center justify-end gap-2">
          {isEditing ? (
            <Button variant="ghost" onClick={resetForm}>
              <X className="size-4" />
              Cancel
            </Button>
          ) : null}
          <Button
            onClick={() => saveMutation.mutate()}
            loading={saveMutation.isPending}
            disabled={!canSave}
          >
            {isEditing ? <Pencil className="size-4" /> : <Plus className="size-4" />}
            {isEditing ? "Save changes" : "Add template"}
          </Button>
        </div>
      </Card>

      <div>
        <h2 className="text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)] mb-2">
          {templates.length} template{templates.length === 1 ? "" : "s"}
        </h2>
        {templatesQuery.isLoading ? (
          <div className="flex justify-center py-10">
            <Spinner size={24} />
          </div>
        ) : templates.length === 0 ? (
          <p className="text-sm text-[color:var(--color-muted-fg)]">
            No templates yet. Add one above.
          </p>
        ) : (
          <ul className="space-y-2">
            {templates.map((t) => (
              <li
                key={t.templateId}
                className="rounded-md border border-[color:var(--color-border)] p-3 flex items-start justify-between gap-3 bg-[color:var(--color-surface)]"
              >
                <div className="min-w-0">
                  <div className="text-sm font-medium">{t.label}</div>
                  <div className="text-xs text-[color:var(--color-muted-fg)] mt-0.5">
                    {t.body}
                  </div>
                </div>
                <div className="flex items-center gap-1 shrink-0">
                  <Button variant="ghost" size="sm" onClick={() => startEdit(t)}>
                    <Pencil className="size-4" />
                  </Button>
                  <Button
                    variant="ghost"
                    size="sm"
                    onClick={() => setConfirmDelete(t)}
                  >
                    <Trash2 className="size-4" />
                  </Button>
                </div>
              </li>
            ))}
          </ul>
        )}
      </div>

      <Dialog
        open={confirmDelete !== null}
        onClose={() => setConfirmDelete(null)}
        title="Remove this template?"
        description={
          confirmDelete
            ? `"${confirmDelete.label}" will no longer appear in the wizard.`
            : ""
        }
      >
        <DialogActions>
          <Button variant="ghost" onClick={() => setConfirmDelete(null)}>
            Keep it
          </Button>
          <Button
            variant="danger"
            loading={deleteMutation.isPending}
            onClick={() => {
              if (confirmDelete) deleteMutation.mutate(confirmDelete.templateId);
            }}
          >
            Remove
          </Button>
        </DialogActions>
      </Dialog>
    </div>
  );
}
