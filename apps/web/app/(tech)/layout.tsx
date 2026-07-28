"use client";

import { LogOut } from "lucide-react";
import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useEffect } from "react";

import { NavDropdown, NavLink } from "@/components/nav-dropdown";
import { StatusBanner } from "@/components/status-banner";
import { ThemeToggle } from "@/components/theme-toggle";
import { Button } from "@/components/ui/button";
import { Spinner } from "@/components/ui/spinner";
import { canVisit, roleHome, TECH_NAV } from "@/lib/access";
import { useAuth } from "@/lib/auth-context";
import { isNrs, roleLabel } from "@/lib/types";

export default function TechLayout({ children }: { children: React.ReactNode }) {
  const router = useRouter();
  const pathname = usePathname();
  const { user, isAuthenticated, isHydrated, logout } = useAuth();

  useEffect(() => {
    if (isHydrated && !isAuthenticated) {
      router.replace("/login");
    }
  }, [isHydrated, isAuthenticated, router]);

  // Centralized role guard: a page the role can't visit never mounts (so its
  // queries never fire) — the user is silently sent to their role's home.
  const allowed = user ? canVisit(user.role, pathname) : true;

  useEffect(() => {
    if (isHydrated && user && !allowed) {
      router.replace(roleHome(user.role));
    }
  }, [isHydrated, user, allowed, router]);

  if (!isHydrated || !isAuthenticated || !user) {
    return (
      <div className="min-h-screen flex items-center justify-center">
        <Spinner size={28} />
      </div>
    );
  }

  return (
    <div className="min-h-screen flex flex-col">
      <StatusBanner />
      <header className="border-b border-[color:var(--color-border)] bg-[color:var(--color-surface)]/80 backdrop-blur supports-[backdrop-filter]:bg-[color:var(--color-surface)]/60 sticky top-0 z-30">
        <div className="mx-auto max-w-7xl px-6 h-14 flex items-center justify-between">
          <div className="flex items-center gap-4">
            <Link
              href={roleHome(user.role)}
              className="flex items-center gap-2 group"
              aria-label="Radiology Plus home"
            >
              <span className="inline-block w-2 h-2 rounded-full bg-[color:var(--color-accent)] group-hover:scale-110 transition-transform" />
              <span
                className="text-base"
                style={{ fontFamily: "var(--font-display)" }}
              >
                Radiology Plus
              </span>
            </Link>
            <nav className="hidden md:flex items-center gap-1 text-sm">
              {TECH_NAV.filter((entry) => entry.visible(user.role)).map((entry) =>
                entry.items ? (
                  <NavDropdown key={entry.label} label={entry.label} items={entry.items} />
                ) : (
                  <NavLink key={entry.label} href={entry.href!} label={entry.label} />
                ),
              )}
            </nav>
          </div>

          <div className="flex items-center gap-3">
            <ThemeToggle />
            <div className="hidden sm:flex flex-col items-end leading-tight">
              <span className="text-sm font-medium">
                {user.displayName ?? user.username}
              </span>
              <span className="text-[10px] uppercase tracking-[0.2em] text-[color:var(--color-muted-fg)]">
                {roleLabel(user.role)}
                {isNrs(user.role) ? " · NRS" : ""}
              </span>
            </div>
            <Button
              variant="ghost"
              size="sm"
              onClick={() => {
                logout().finally(() => router.replace("/login"));
              }}
              aria-label="Sign out"
            >
              <LogOut className="size-4" />
              <span className="hidden sm:inline">Sign out</span>
            </Button>
          </div>
        </div>
      </header>

      <main className="flex-1">
        {allowed ? (
          children
        ) : (
          <div className="min-h-[60vh] flex items-center justify-center">
            <Spinner size={28} />
          </div>
        )}
      </main>
    </div>
  );
}
