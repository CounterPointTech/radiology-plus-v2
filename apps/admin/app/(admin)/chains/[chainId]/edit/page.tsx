"use client";

import { useQuery } from "@tanstack/react-query";
import { useParams, useRouter } from "next/navigation";
import { useEffect } from "react";

import { ChainEditor, chainFormFromDetail } from "@/components/chains/chain-editor";
import { Spinner } from "@/components/ui/spinner";
import { chainsApi } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { canAccessScripting } from "@/lib/types";

export default function EditChainPage() {
  const router = useRouter();
  const { chainId } = useParams<{ chainId: string }>();
  const { user, isHydrated } = useAuth();

  useEffect(() => {
    if (isHydrated && user && !canAccessScripting(user.role)) {
      router.replace("/notifications");
    }
  }, [isHydrated, user, router]);

  const chain = useQuery({
    queryKey: ["chain", chainId],
    queryFn: () => chainsApi.get(chainId),
    enabled: !!user && canAccessScripting(user.role),
  });

  if (!isHydrated || !user || chain.isLoading) {
    return (
      <div className="min-h-[60vh] flex items-center justify-center">
        <Spinner size={28} />
      </div>
    );
  }

  if (chain.isError || !chain.data) {
    return (
      <div className="mx-auto max-w-4xl px-6 py-16 text-center">
        <p className="text-sm text-[color:var(--color-muted-fg)]">
          Couldn&apos;t load that chain — it may have been deleted.
        </p>
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
          Edit chain
        </h1>
        <p className="text-sm text-[color:var(--color-muted-fg)] mt-1">{chain.data.name}</p>
      </div>
      <ChainEditor chainId={chainId} initial={chainFormFromDetail(chain.data)} />
    </div>
  );
}
