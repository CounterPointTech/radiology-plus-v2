"use client";

import { LogOut, ShieldAlert } from "lucide-react";
import type { Route } from "next";
import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useEffect } from "react";

import { Button } from "@/components/ui/button";
import { Spinner } from "@/components/ui/spinner";
import { useAuth } from "@/lib/auth-context";
import { canAccessAdmin, canAccessScripting, isNrs, roleLabel } from "@/lib/types";

interface NavItem {
  href: Route;
  label: string;
}

const activeCls = "text-[color:var(--color-accent)] bg-[color:var(--color-accent)]/10";
const idleCls =
  "text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-base-fg)] hover:bg-[color:var(--color-surface-2)]";

function isRouteActive(pathname: string, href: string): boolean {
  return pathname === href || pathname.startsWith(href + "/");
}

function NavLink({ href, label }: NavItem) {
  const pathname = usePathname();
  const active = isRouteActive(pathname, href);
  return (
    <Link
      href={href}
      className={`px-3 py-1.5 rounded-md ${active ? activeCls : idleCls}`}
    >
      {label}
    </Link>
  );
}

/**
 * The technical console shell. The WHOLE app is gated to NRS/Admin — other roles
 * get an explicit no-access screen (the server enforces the same on every endpoint).
 */
export default function AdminLayout({ children }: { children: React.ReactNode }) {
  const router = useRouter();
  const { user, isAuthenticated, isHydrated, logout } = useAuth();

  useEffect(() => {
    if (isHydrated && !isAuthenticated) {
      router.replace("/login");
    }
  }, [isHydrated, isAuthenticated, router]);

  if (!isHydrated || !isAuthenticated || !user) {
    return (
      <div className="min-h-screen flex items-center justify-center">
        <Spinner size={28} />
      </div>
    );
  }

  if (!canAccessAdmin(user.role)) {
    return (
      <div className="min-h-screen flex items-center justify-center px-6">
        <div className="max-w-md text-center space-y-4">
          <ShieldAlert className="size-10 mx-auto text-[color:var(--color-accent)]" />
          <h1 className="text-2xl" style={{ fontFamily: "var(--font-display)" }}>
            This is the technical console
          </h1>
          <p className="text-sm text-[color:var(--color-muted-fg)]">
            Your account ({roleLabel(user.role)}) doesn&apos;t have access to this site.
            The clinical app is where validation and billing live.
          </p>
          <Button
            variant="secondary"
            onClick={() => {
              logout().finally(() => router.replace("/login"));
            }}
          >
            <LogOut className="size-4" />
            Sign out
          </Button>
        </div>
      </div>
    );
  }

  return (
    <div className="min-h-screen flex flex-col">
      <header className="border-b border-[color:var(--color-border)] bg-[color:var(--color-surface)]/80 backdrop-blur supports-[backdrop-filter]:bg-[color:var(--color-surface)]/60 sticky top-0 z-30">
        <div className="mx-auto max-w-7xl px-6 h-14 flex items-center justify-between">
          <div className="flex items-center gap-4">
            <Link
              href="/scripts"
              className="flex items-center gap-2 group"
              aria-label="Radiology Plus Admin home"
            >
              <span className="inline-block w-2 h-2 rounded-full bg-[color:var(--color-accent)] group-hover:scale-110 transition-transform" />
              <span
                className="text-base"
                style={{ fontFamily: "var(--font-display)" }}
              >
                Radiology Plus <span className="text-[color:var(--color-muted-fg)]">Admin</span>
              </span>
            </Link>
            <nav className="hidden md:flex items-center gap-1 text-sm">
              {canAccessScripting(user.role) ? (
                <NavLink href="/scripts" label="Script Manager" />
              ) : null}
              <NavLink href="/notifications" label="Notifications" />
              <NavLink href="/admin/users" label="Users" />
              <NavLink href="/admin/facilities" label="Facilities" />
              <NavLink href="/admin/settings" label="Settings" />
            </nav>
          </div>

          <div className="flex items-center gap-3">
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

      <main className="flex-1">{children}</main>
    </div>
  );
}
