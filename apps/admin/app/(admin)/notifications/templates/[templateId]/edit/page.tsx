"use client";

import { useQuery } from "@tanstack/react-query";
import { useParams } from "next/navigation";

import {
  TemplateEditor,
  templateFormFromDetail,
} from "@/components/notifications/template-editor";
import { Spinner } from "@/components/ui/spinner";
import { notificationsApi } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";

export default function EditTemplatePage() {
  const { templateId } = useParams<{ templateId: string }>();
  const { user, isHydrated } = useAuth();

  const template = useQuery({
    queryKey: ["notif-template", templateId],
    queryFn: () => notificationsApi.template(templateId),
    enabled: !!user,
  });

  if (!isHydrated || !user || template.isLoading) {
    return (
      <div className="min-h-[60vh] flex items-center justify-center">
        <Spinner size={28} />
      </div>
    );
  }

  if (template.isError || !template.data) {
    return (
      <p className="py-16 text-center text-sm text-[color:var(--color-muted-fg)]">
        Couldn&apos;t load that template — it may have been deleted.
      </p>
    );
  }

  return (
    <TemplateEditor templateId={templateId} initial={templateFormFromDetail(template.data)} />
  );
}
