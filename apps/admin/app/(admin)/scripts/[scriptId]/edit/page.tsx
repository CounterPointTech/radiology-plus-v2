"use client";

import { useQuery } from "@tanstack/react-query";
import { useParams, useRouter } from "next/navigation";
import { useEffect } from "react";

import { initialFromDetail, ScriptEditor } from "@/components/scripts/script-editor";
import { Spinner } from "@/components/ui/spinner";
import { scriptsApi } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { canAccessScripting } from "@/lib/types";

export default function EditScriptPage() {
  const router = useRouter();
  const { scriptId } = useParams<{ scriptId: string }>();
  const { user, isHydrated } = useAuth();

  useEffect(() => {
    if (isHydrated && user && !canAccessScripting(user.role)) {
      router.replace("/notifications");
    }
  }, [isHydrated, user, router]);

  const script = useQuery({
    queryKey: ["script", scriptId],
    queryFn: () => scriptsApi.get(scriptId),
    enabled: !!user && canAccessScripting(user.role),
  });

  if (!isHydrated || !user || script.isLoading) {
    return (
      <div className="min-h-[60vh] flex items-center justify-center">
        <Spinner size={28} />
      </div>
    );
  }

  if (script.isError || !script.data) {
    return (
      <div className="mx-auto max-w-4xl px-6 py-16 text-center">
        <p className="text-sm text-[color:var(--color-muted-fg)]">
          Couldn&apos;t load that script — it may have been deleted.
        </p>
      </div>
    );
  }

  return (
    <div className="mx-auto max-w-4xl px-6 py-8 rise-in">
      <div className="mb-6">
        <p className="text-[10px] uppercase tracking-[0.3em] text-[color:var(--color-accent)]">
          Script Manager
        </p>
        <h1 className="text-3xl mt-1" style={{ fontFamily: "var(--font-display)" }}>
          Edit script
        </h1>
        <p className="text-sm text-[color:var(--color-muted-fg)] mt-1">{script.data.name}</p>
      </div>
      <ScriptEditor scriptId={scriptId} initial={initialFromDetail(script.data)} />
    </div>
  );
}
