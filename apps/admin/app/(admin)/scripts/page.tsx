"use client";

import { useMutation, useQuery } from "@tanstack/react-query";
import { CheckCircle2, FlaskConical, ScrollText, XCircle } from "lucide-react";
import { useRouter } from "next/navigation";
import { useEffect } from "react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Spinner } from "@/components/ui/spinner";
import { adminApi } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { canAccessScripting } from "@/lib/types";

export default function ScriptsPage() {
  const router = useRouter();
  const { user, isHydrated } = useAuth();

  // Script Manager is NRS-only; Admin-role users get bounced to Notifications.
  useEffect(() => {
    if (isHydrated && user && !canAccessScripting(user.role)) {
      router.replace("/notifications");
    }
  }, [isHydrated, user, router]);

  const status = useQuery({
    queryKey: ["scripts-status"],
    queryFn: () => adminApi.scriptsStatus(),
    enabled: !!user && canAccessScripting(user.role),
  });

  const smokeTest = useMutation({
    mutationFn: () => adminApi.runScriptSmokeTest(),
  });

  if (!isHydrated || !user) {
    return (
      <div className="min-h-[60vh] flex items-center justify-center">
        <Spinner size={28} />
      </div>
    );
  }

  return (
    <div className="mx-auto max-w-5xl px-6 py-8">
      <div className="mb-6">
        <p className="text-[10px] uppercase tracking-[0.3em] text-[color:var(--color-accent)]">
          Technical
        </p>
        <h1 className="text-3xl mt-1" style={{ fontFamily: "var(--font-display)" }}>
          Script Manager
        </h1>
        <p className="text-sm text-[color:var(--color-muted-fg)] mt-1 max-w-2xl">
          Author, schedule, and run workflow scripts against facility systems.
        </p>
      </div>

      <div className="rounded-lg border border-[color:var(--color-accent)]/40 bg-[color:var(--color-accent)]/10 px-4 py-3 mb-6">
        <div className="flex items-center gap-2 text-sm font-medium">
          <ScrollText className="size-4 text-[color:var(--color-accent)]" />
          Coming soon
        </div>
        <p className="text-sm text-[color:var(--color-muted-fg)] mt-1 ml-6">
          {status.data?.message ??
            "The Script Manager UI is being built. The execution engine underneath is already live."}
        </p>
        {status.data ? (
          <div className="flex items-center gap-2 mt-2 ml-6">
            <span className="text-xs text-[color:var(--color-muted-fg)]">Engines:</span>
            {status.data.supportedLanguages.map((l) => (
              <Badge key={l} variant="accent">
                {l}
              </Badge>
            ))}
          </div>
        ) : null}
      </div>

      <div className="rounded-lg border border-[color:var(--color-border)] bg-[color:var(--color-surface)] p-4 space-y-3">
        <div className="flex items-center gap-2">
          <FlaskConical className="size-4 text-[color:var(--color-accent)]" />
          <h2 className="text-sm font-medium">Engine smoke test</h2>
        </div>
        <p className="text-xs text-[color:var(--color-muted-fg)]">
          Runs an inline <code className="font-mono">SELECT 1</code> through the real
          PostgreSQL script executor to prove the engine, database connection, and audit
          trail all work on this host.
        </p>
        <Button
          variant="secondary"
          size="sm"
          loading={smokeTest.isPending}
          onClick={() => smokeTest.mutate()}
        >
          Run smoke test
        </Button>

        {smokeTest.isError ? (
          <p className="text-sm text-[color:var(--color-novarad-red)]">
            The test call failed — is the AdminApi running and are you signed in as NRS?
          </p>
        ) : null}
        {smokeTest.data ? (
          smokeTest.data.ok ? (
            <div className="text-sm space-y-1">
              <p className="flex items-center gap-2">
                <CheckCircle2 className="size-4 text-[oklch(0.72_0.14_160)]" />
                Engine executed in {smokeTest.data.durationMs}ms.
              </p>
              {smokeTest.data.output ? (
                <pre className="text-xs text-[color:var(--color-muted-fg)] bg-[color:var(--color-surface-2)] rounded-md p-2 overflow-x-auto">
                  {smokeTest.data.output}
                </pre>
              ) : null}
            </div>
          ) : (
            <p className="text-sm flex items-center gap-2 text-[color:var(--color-novarad-red)]">
              <XCircle className="size-4" />
              {smokeTest.data.error ?? `Engine returned ${smokeTest.data.status}.`}
            </p>
          )
        ) : null}
      </div>
    </div>
  );
}
