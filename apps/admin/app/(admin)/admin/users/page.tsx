"use client";

import { Users } from "lucide-react";

export default function UsersPage() {
  return (
    <div className="mx-auto max-w-5xl px-6 py-8">
      <div className="mb-6">
        <p className="text-[10px] uppercase tracking-[0.3em] text-[color:var(--color-accent)]">
          Administration
        </p>
        <h1 className="text-3xl mt-1" style={{ fontFamily: "var(--font-display)" }}>
          Users
        </h1>
        <p className="text-sm text-[color:var(--color-muted-fg)] mt-1 max-w-2xl">
          Manage local accounts and review federated NovaRad sign-ins.
        </p>
      </div>

      <div className="rounded-lg border border-[color:var(--color-accent)]/40 bg-[color:var(--color-accent)]/10 px-4 py-3">
        <div className="flex items-center gap-2 text-sm font-medium">
          <Users className="size-4 text-[color:var(--color-accent)]" />
          Coming soon
        </div>
        <p className="text-sm text-[color:var(--color-muted-fg)] mt-1 ml-6">
          User management is being built as part of the technical console rollout.
        </p>
      </div>
    </div>
  );
}
