"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AxiosError } from "axios";
import { FileText, Pencil, Plus, Trash2 } from "lucide-react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";

import { ChannelBadge } from "@/components/notifications/notification-bits";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Spinner } from "@/components/ui/spinner";
import { notificationsApi } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { formatDateTime } from "@/lib/utils";

export default function NotificationTemplatesPage() {
  const router = useRouter();
  const qc = useQueryClient();
  const { user, isHydrated } = useAuth();
  const [confirmDeleteId, setConfirmDeleteId] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);

  const templates = useQuery({
    queryKey: ["notif-templates"],
    queryFn: () => notificationsApi.templates(),
    enabled: !!user,
  });
  const invalidate = () => qc.invalidateQueries({ queryKey: ["notif-templates"] });

  const toggleMut = useMutation({
    mutationFn: (v: { id: string; isActive: boolean }) =>
      notificationsApi.setTemplateActive(v.id, v.isActive),
    onSuccess: invalidate,
    onError: () => setActionError("Couldn't update the template. Try again."),
  });
  const deleteMut = useMutation({
    mutationFn: (id: string) => notificationsApi.deleteTemplate(id),
    onSuccess: () => {
      setConfirmDeleteId(null);
      void invalidate();
    },
    onError: (err) => {
      const ax = err as AxiosError<{ error?: string }>;
      setActionError(
        ax.response?.data?.error ?? "Couldn't delete the template. Try again.",
      );
      setConfirmDeleteId(null);
    },
  });

  if (!isHydrated || !user) {
    return (
      <div className="min-h-[60vh] flex items-center justify-center">
        <Spinner size={28} />
      </div>
    );
  }

  const rows = templates.data ?? [];

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <p className="text-sm text-[color:var(--color-muted-fg)]">
          Reusable Handlebars messages — compose from one, or let scripts and system
          events fill in the variables.
        </p>
        <Button size="sm" onClick={() => router.push("/notifications/templates/new")}>
          <Plus className="size-4" />
          New template
        </Button>
      </div>

      {actionError ? (
        <p className="text-sm text-[color:var(--color-novarad-red)]">{actionError}</p>
      ) : null}

      {templates.isLoading ? (
        <div className="min-h-[30vh] flex items-center justify-center">
          <Spinner size={24} />
        </div>
      ) : templates.isError ? (
        <p className="text-sm text-[color:var(--color-novarad-red)]">
          Couldn&apos;t load templates.{" "}
          <button className="underline underline-offset-2" onClick={() => templates.refetch()}>
            Try again
          </button>
        </p>
      ) : rows.length === 0 ? (
        <div className="rounded-lg border border-dashed border-[color:var(--color-border)] px-6 py-16 text-center rise-in">
          <FileText className="size-8 mx-auto text-[color:var(--color-accent)]" />
          <p className="mt-3 text-sm text-[color:var(--color-muted-fg)]">
            No templates yet. Create the first one — the editor shows a live rendered preview.
          </p>
          <Button className="mt-4" size="sm" onClick={() => router.push("/notifications/templates/new")}>
            <Plus className="size-4" />
            New template
          </Button>
        </div>
      ) : (
        <ul className="space-y-2">
          {rows.map((t) => (
            <li
              key={t.templateId}
              className="flex flex-wrap items-center gap-x-3 gap-y-2 rounded-lg border border-[color:var(--color-border)] bg-[color:var(--color-surface)] px-4 py-2.5 transition-[border-color] hover:border-[color:var(--color-accent)]/40"
            >
              <Link
                href={`/notifications/templates/${t.templateId}/edit` as never}
                className="min-w-0 flex-1 truncate font-medium hover:text-[color:var(--color-accent)] transition-colors"
                style={{ fontFamily: "var(--font-display)" }}
              >
                {t.name}
              </Link>
              <ChannelBadge channel={t.channel} />
              {t.isHtml ? <Badge variant="neutral">HTML</Badge> : null}
              {t.isActive ? null : <Badge variant="neutral">inactive</Badge>}
              <span className="text-xs text-[color:var(--color-muted-fg)]">
                created {formatDateTime(t.createdAt)}
              </span>

              <div className="flex items-center gap-1.5">
                {confirmDeleteId === t.templateId ? (
                  <>
                    <span className="text-xs text-[color:var(--color-caution)]">
                      Delete this template?
                    </span>
                    <Button
                      variant="danger"
                      size="sm"
                      loading={deleteMut.isPending}
                      onClick={() => deleteMut.mutate(t.templateId)}
                    >
                      Confirm
                    </Button>
                    <Button variant="ghost" size="sm" onClick={() => setConfirmDeleteId(null)}>
                      Cancel
                    </Button>
                  </>
                ) : (
                  <>
                    <Button
                      variant="ghost"
                      size="sm"
                      loading={toggleMut.isPending && toggleMut.variables?.id === t.templateId}
                      onClick={() =>
                        toggleMut.mutate({ id: t.templateId, isActive: !t.isActive })
                      }
                    >
                      {t.isActive ? "Deactivate" : "Activate"}
                    </Button>
                    <Link
                      href={`/notifications/templates/${t.templateId}/edit` as never}
                      className="inline-flex items-center gap-1 rounded-md px-2.5 py-1.5 text-sm text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-base-fg)] hover:bg-[color:var(--color-surface-2)] transition-colors"
                    >
                      <Pencil className="size-3.5" /> Edit
                    </Link>
                    <button
                      type="button"
                      onClick={() => setConfirmDeleteId(t.templateId)}
                      title="Delete"
                      aria-label={`Delete ${t.name}`}
                      className="inline-flex items-center rounded-md p-1.5 text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-novarad-red)] hover:bg-[color:var(--color-surface-2)] transition-colors"
                    >
                      <Trash2 className="size-3.5" />
                    </button>
                  </>
                )}
              </div>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
