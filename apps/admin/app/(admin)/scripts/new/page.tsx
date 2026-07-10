"use client";

import { useQuery } from "@tanstack/react-query";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useEffect } from "react";

import { EMPTY_INITIAL, initialFromDetail, ScriptEditor } from "@/components/scripts/script-editor";
import { Spinner } from "@/components/ui/spinner";
import { scriptsApi } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { canAccessScripting } from "@/lib/types";

function NewScriptInner() {
  const router = useRouter();
  const params = useSearchParams();
  const { user, isHydrated } = useAuth();

  useEffect(() => {
    if (isHydrated && user && !canAccessScripting(user.role)) {
      router.replace("/notifications");
    }
  }, [isHydrated, user, router]);

  // ?from=<scriptId> = duplicate an existing script as the starting point.
  const fromId = params.get("from");
  const source = useQuery({
    queryKey: ["script", fromId],
    queryFn: () => scriptsApi.get(fromId!),
    enabled: fromId !== null && !!user && canAccessScripting(user.role),
  });

  if (!isHydrated || !user || (fromId !== null && source.isLoading)) {
    return (
      <div className="min-h-[60vh] flex items-center justify-center">
        <Spinner size={28} />
      </div>
    );
  }

  const initial = source.data ? initialFromDetail(source.data, true) : EMPTY_INITIAL;

  return (
    <div className="mx-auto max-w-4xl px-6 py-8 rise-in">
      <div className="mb-6">
        <p className="text-[10px] uppercase tracking-[0.3em] text-[color:var(--color-accent)]">
          Script Manager
        </p>
        <h1 className="text-3xl mt-1" style={{ fontFamily: "var(--font-display)" }}>
          {source.data ? "Duplicate script" : "New script"}
        </h1>
        {source.data ? (
          <p className="text-sm text-[color:var(--color-muted-fg)] mt-1">
            Starting from “{source.data.name}”.
          </p>
        ) : null}
      </div>
      <ScriptEditor key={fromId ?? "blank"} initial={initial} />
    </div>
  );
}

export default function NewScriptPage() {
  return (
    <Suspense
      fallback={
        <div className="min-h-[60vh] flex items-center justify-center">
          <Spinner size={28} />
        </div>
      }
    >
      <NewScriptInner />
    </Suspense>
  );
}
