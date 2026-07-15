"use client";

import { Sparkles } from "lucide-react";

import { useAuth } from "@/lib/auth-context";

// Radiologist home. The radiologist workspace isn't built yet, so this page
// greets them warmly instead of dropping them into surfaces they can't use.
// When Rad validation ships, it will live under /rad and replace this.
export default function RadHomePage() {
  const { user } = useAuth();
  const firstName = (user?.displayName ?? user?.username ?? "").split(" ")[0];

  return (
    <div className="mx-auto max-w-2xl px-6 py-24 text-center space-y-6">
      <p className="text-xs uppercase tracking-[0.2em] text-[color:var(--color-accent)]">
        Radiology Plus
      </p>
      <h1
        className="text-4xl leading-tight font-medium"
        style={{ fontFamily: "var(--font-display)" }}
      >
        {firstName ? `Welcome, ${firstName}.` : "Welcome."}
      </h1>
      <div className="inline-flex items-center gap-2 rounded-full border border-[color:var(--color-accent)]/40 bg-[color:var(--color-accent)]/10 px-4 py-1.5 text-sm text-[color:var(--color-accent)]">
        <Sparkles className="size-4" />
        Coming soon
      </div>
      <p className="text-lg text-[color:var(--color-muted-fg)]">
        Your radiologist workspace is on its way. When it&apos;s ready, this is
        where you&apos;ll see the studies waiting for you.
      </p>
      <p className="text-sm text-[color:var(--color-muted-fg)]">
        Your account is set up and ready — there&apos;s nothing you need to do.
        Questions? Contact your site administrator.
      </p>
    </div>
  );
}
