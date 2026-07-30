"use client";

import { AnimatePresence, motion } from "framer-motion";
import {
  BellRing,
  Building2,
  LogOut,
  ScrollText,
  Settings,
  ShieldAlert,
  Terminal,
  Users,
  Workflow,
} from "lucide-react";
import type { Route } from "next";
import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useEffect, useState } from "react";

import { ThemeToggle } from "@/components/theme-toggle";
import { Button } from "@/components/ui/button";
import { Spinner } from "@/components/ui/spinner";
import { useAuth } from "@/lib/auth-context";
import { canAccessAdmin, canAccessScripting, roleLabel } from "@/lib/types";

const COLLAPSE_KEY = "radplus.admin.sidebar";

interface NavEntry {
  href: Route;
  label: string;
  icon: React.ComponentType<{ className?: string }>;
  nrsOnly?: boolean;
}

const NAV: NavEntry[] = [
  { href: "/scripts", label: "Script Manager", icon: Terminal, nrsOnly: true },
  { href: "/chains", label: "Script Chains", icon: Workflow, nrsOnly: true },
  { href: "/notifications", label: "Notifications", icon: BellRing },
  { href: "/admin/users", label: "Users", icon: Users },
  { href: "/admin/facilities", label: "Facilities", icon: Building2 },
  { href: "/admin/audit", label: "Audit Log", icon: ScrollText },
  { href: "/admin/settings", label: "Settings", icon: Settings },
];

function isRouteActive(pathname: string, href: string): boolean {
  return pathname === href || pathname.startsWith(href + "/");
}

/** Animated hamburger — three bars morph into an X when open. */
function Hamburger({ open, onClick, label }: { open: boolean; onClick: () => void; label: string }) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-label={label}
      aria-expanded={open}
      className="relative size-9 shrink-0 rounded-md hover:bg-[color:var(--color-surface-2)] transition-colors"
    >
      <span
        className={`absolute left-1/2 top-1/2 h-0.5 w-4.5 -translate-x-1/2 rounded-full bg-current transition-all duration-300 ${
          open ? "rotate-45" : "-translate-y-[5.5px]"
        }`}
      />
      <span
        className={`absolute left-1/2 top-1/2 h-0.5 w-4.5 -translate-x-1/2 rounded-full bg-current transition-all duration-300 ${
          open ? "opacity-0 scale-x-0" : "opacity-100"
        }`}
      />
      <span
        className={`absolute left-1/2 top-1/2 h-0.5 w-4.5 -translate-x-1/2 rounded-full bg-current transition-all duration-300 ${
          open ? "-rotate-45" : "translate-y-[5.5px]"
        }`}
      />
    </button>
  );
}

function Wordmark({ collapsed }: { collapsed: boolean }) {
  return (
    <Link href="/scripts" aria-label="Radiology Plus Admin home" className="flex items-center gap-2 min-w-0">
      <span className="inline-block size-2 shrink-0 rounded-full bg-[color:var(--color-accent)] shadow-[var(--glow-accent-strong)]" />
      {collapsed ? null : (
        <span className="truncate text-sm" style={{ fontFamily: "var(--font-display)" }}>
          rad+<span className="text-[color:var(--color-accent)]">/admin</span>
          <span className="caret-blink text-[color:var(--color-accent)]">▍</span>
        </span>
      )}
    </Link>
  );
}

function SidebarNav({
  role,
  collapsed,
  onNavigate,
}: {
  role: string;
  collapsed: boolean;
  onNavigate?: () => void;
}) {
  const pathname = usePathname();
  const items = NAV.filter((n) => !n.nrsOnly || canAccessScripting(role));

  return (
    <motion.nav
      initial="hidden"
      animate="show"
      variants={{ hidden: {}, show: { transition: { staggerChildren: 0.05, delayChildren: 0.1 } } }}
      className="flex flex-col gap-1 px-2"
      aria-label="Console navigation"
    >
      {items.map((item) => {
        const active = isRouteActive(pathname, item.href);
        const Icon = item.icon;
        return (
          <motion.div
            key={item.href}
            variants={{ hidden: { opacity: 0, x: -12 }, show: { opacity: 1, x: 0 } }}
            className="relative"
          >
            {active ? (
              <motion.span
                layoutId="nav-active"
                transition={{ type: "spring", stiffness: 500, damping: 40 }}
                className="absolute inset-0 rounded-md bg-[color:var(--color-accent)]/12 border border-[color:var(--color-accent)]/25 shadow-[var(--glow-accent)]"
              />
            ) : null}
            <Link
              href={item.href}
              onClick={onNavigate}
              title={collapsed ? item.label : undefined}
              className={`relative flex items-center gap-3 rounded-md px-2.5 py-2 text-sm transition-colors ${
                active
                  ? "text-[color:var(--color-accent)]"
                  : "text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-base-fg)] hover:bg-[color:var(--color-surface-2)]"
              } ${collapsed ? "justify-center px-0" : ""}`}
            >
              <Icon className="size-4 shrink-0" />
              {collapsed ? null : <span className="truncate">{item.label}</span>}
            </Link>
          </motion.div>
        );
      })}
    </motion.nav>
  );
}

export default function AdminLayout({ children }: { children: React.ReactNode }) {
  const router = useRouter();
  const pathname = usePathname();
  const { user, isAuthenticated, isHydrated, logout } = useAuth();

  const [collapsed, setCollapsed] = useState(false);
  const [mobileOpen, setMobileOpen] = useState(false);

  useEffect(() => {
    setCollapsed(window.localStorage.getItem(COLLAPSE_KEY) === "rail");
  }, []);

  useEffect(() => {
    if (isHydrated && !isAuthenticated) {
      router.replace("/login");
    }
  }, [isHydrated, isAuthenticated, router]);

  // Close the mobile drawer whenever the route changes.
  useEffect(() => {
    setMobileOpen(false);
  }, [pathname]);

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
        <div className="max-w-md text-center space-y-4 rise-in">
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

  function toggleCollapsed() {
    setCollapsed((v) => {
      window.localStorage.setItem(COLLAPSE_KEY, v ? "open" : "rail");
      return !v;
    });
  }

  const sidebarFooter = (
    <div className="mt-auto flex flex-col gap-1 px-2 pb-3">
      <ThemeToggle collapsed={collapsed} />
      <div
        className={`flex items-center gap-2 rounded-md px-2.5 py-2 ${collapsed ? "justify-center px-0" : ""}`}
        title={user.displayName ?? user.username}
      >
        <span className="flex size-6 shrink-0 items-center justify-center rounded-full bg-[color:var(--color-accent)]/15 text-[10px] font-medium text-[color:var(--color-accent)]">
          {(user.displayName ?? user.username).slice(0, 2).toUpperCase()}
        </span>
        {collapsed ? null : (
          <span className="min-w-0 leading-tight">
            <span className="block truncate text-xs font-medium">
              {user.displayName ?? user.username}
            </span>
            {/* Just the role. The old " · NRS" suffix could only ever fire when
                roleLabel had already printed "NRS", so it rendered "NRS · NRS". */}
            <span className="block text-[9px] uppercase tracking-[0.2em] text-[color:var(--color-muted-fg)]">
              {roleLabel(user.role)}
            </span>
          </span>
        )}
      </div>
      <button
        type="button"
        onClick={() => {
          logout().finally(() => router.replace("/login"));
        }}
        title={collapsed ? "Sign out" : undefined}
        className={`inline-flex items-center gap-2 rounded-md px-2.5 py-2 text-sm text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-base-fg)] hover:bg-[color:var(--color-surface-2)] transition-colors ${
          collapsed ? "justify-center px-0" : ""
        }`}
      >
        <LogOut className="size-4 shrink-0" />
        {collapsed ? null : <span>Sign out</span>}
      </button>
    </div>
  );

  return (
    <div className="min-h-screen md:flex">
      {/* Desktop sidebar — collapsible to an icon rail */}
      <motion.aside
        initial={false}
        animate={{ width: collapsed ? 56 : 232 }}
        transition={{ type: "spring", stiffness: 320, damping: 34 }}
        className="hidden md:flex sticky top-0 h-screen shrink-0 flex-col overflow-hidden border-r border-[color:var(--color-border)] bg-[color:var(--color-surface)]/70 backdrop-blur"
      >
        <div className={`flex h-14 items-center gap-1 px-3 ${collapsed ? "justify-center px-0" : ""}`}>
          <Hamburger
            open={!collapsed}
            onClick={toggleCollapsed}
            label={collapsed ? "Expand navigation" : "Collapse navigation"}
          />
          {collapsed ? null : <Wordmark collapsed={false} />}
        </div>
        <div className="flex-1 flex flex-col overflow-y-auto pt-2">
          <SidebarNav role={user.role} collapsed={collapsed} />
          {sidebarFooter}
        </div>
      </motion.aside>

      {/* Mobile top bar + drawer */}
      <header className="md:hidden sticky top-0 z-40 flex h-14 items-center gap-2 border-b border-[color:var(--color-border)] bg-[color:var(--color-surface)]/85 backdrop-blur px-3">
        <Hamburger open={mobileOpen} onClick={() => setMobileOpen((v) => !v)} label="Toggle navigation" />
        <Wordmark collapsed={false} />
      </header>

      <AnimatePresence>
        {mobileOpen ? (
          <>
            <motion.div
              key="scrim"
              initial={{ opacity: 0 }}
              animate={{ opacity: 1 }}
              exit={{ opacity: 0 }}
              onClick={() => setMobileOpen(false)}
              className="md:hidden fixed inset-0 z-40 bg-black/50"
            />
            <motion.aside
              key="drawer"
              initial={{ x: -260 }}
              animate={{ x: 0 }}
              exit={{ x: -260 }}
              transition={{ type: "spring", stiffness: 380, damping: 36 }}
              className="md:hidden fixed inset-y-0 left-0 z-50 flex w-60 flex-col border-r border-[color:var(--color-border)] bg-[color:var(--color-surface)] pt-4"
            >
              <div className="px-4 pb-3">
                <Wordmark collapsed={false} />
              </div>
              <div className="flex-1 flex flex-col overflow-y-auto">
                <SidebarNav role={user.role} collapsed={false} onNavigate={() => setMobileOpen(false)} />
                {sidebarFooter}
              </div>
            </motion.aside>
          </>
        ) : null}
      </AnimatePresence>

      {/* Content */}
      <main className="flex-1 min-w-0">{children}</main>
    </div>
  );
}
