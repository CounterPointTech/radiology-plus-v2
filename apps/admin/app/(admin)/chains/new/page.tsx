"use client";

import { useRouter } from "next/navigation";
import { useEffect } from "react";

import { ChainEditor, EMPTY_CHAIN } from "@/components/chains/chain-editor";
import { Spinner } from "@/components/ui/spinner";
import { useAuth } from "@/lib/auth-context";
import { canAccessScripting } from "@/lib/types";

export default function NewChainPage() {
  const router = useRouter();
  const { user, isHydrated } = useAuth();

  useEffect(() => {
    if (isHydrated && user && !canAccessScripting(user.role)) {
      router.replace("/notifications");
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
    <div className="mx-auto max-w-4xl px-6 py-8 rise-in">
      <div className="mb-6">
        <p className="text-[10px] uppercase tracking-[0.3em] text-[color:var(--color-accent)]">
          Script Chains
        </p>
        <h1 className="text-3xl mt-1" style={{ fontFamily: "var(--font-display)" }}>
          New chain
        </h1>
      </div>
      <ChainEditor initial={EMPTY_CHAIN} />
    </div>
  );
}
