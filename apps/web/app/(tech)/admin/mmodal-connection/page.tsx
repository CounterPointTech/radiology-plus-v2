"use client";

import { useRouter } from "next/navigation";
import { useEffect } from "react";

import { MModalConnectionCard } from "@/components/billing/mmodal-connection-card";
import { Spinner } from "@/components/ui/spinner";
import { useAuth } from "@/lib/auth-context";
import { canAccessAdmin } from "@/lib/types";

export default function MModalConnectionAdminPage() {
  const router = useRouter();
  const { user, isHydrated } = useAuth();

  // Belt-and-suspenders client gate (the server also gates every endpoint on NRS/Admin).
  useEffect(() => {
    if (isHydrated && user && !canAccessAdmin(user.role)) {
      router.replace("/validation");
    }
  }, [isHydrated, user, router]);

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
          Admin
        </p>
        <h1 className="text-3xl mt-1" style={{ fontFamily: "var(--font-display)" }}>
          M*Modal connection
        </h1>
        <p className="text-sm text-[color:var(--color-muted-fg)] mt-1 max-w-2xl">
          Connect Radiology Plus to this site&apos;s M*Modal dictation database so RVU
          values can be synced from the CPT &amp; RVU page. Set this up once per site;
          the password is stored encrypted.
        </p>
      </div>

      <MModalConnectionCard />
    </div>
  );
}
