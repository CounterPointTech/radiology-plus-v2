"use client";

import { useQuery } from "@tanstack/react-query";
import { BellRing } from "lucide-react";

import { Spinner } from "@/components/ui/spinner";
import { adminApi } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";

export default function NotificationsPage() {
  const { user, isHydrated } = useAuth();

  const status = useQuery({
    queryKey: ["notifications-status"],
    queryFn: () => adminApi.notificationsStatus(),
    enabled: !!user,
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
          Notifications
        </h1>
        <p className="text-sm text-[color:var(--color-muted-fg)] mt-1 max-w-2xl">
          Rules and channels for alerting staff when workflow events need attention.
        </p>
      </div>

      <div className="rounded-lg border border-[color:var(--color-accent)]/40 bg-[color:var(--color-accent)]/10 px-4 py-3">
        <div className="flex items-center gap-2 text-sm font-medium">
          <BellRing className="size-4 text-[color:var(--color-accent)]" />
          Coming soon
        </div>
        <p className="text-sm text-[color:var(--color-muted-fg)] mt-1 ml-6">
          {status.data?.message ??
            "Notifications management is being built. The delivery orchestrator already runs in the background service."}
        </p>
      </div>
    </div>
  );
}
