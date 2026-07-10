"use client";

import { EMPTY_TEMPLATE, TemplateEditor } from "@/components/notifications/template-editor";
import { Spinner } from "@/components/ui/spinner";
import { useAuth } from "@/lib/auth-context";

export default function NewTemplatePage() {
  const { user, isHydrated } = useAuth();

  if (!isHydrated || !user) {
    return (
      <div className="min-h-[60vh] flex items-center justify-center">
        <Spinner size={28} />
      </div>
    );
  }

  return <TemplateEditor initial={EMPTY_TEMPLATE} />;
}
