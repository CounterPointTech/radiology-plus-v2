"use client";

import { motion } from "framer-motion";
import type { Route } from "next";
import Link from "next/link";
import { usePathname } from "next/navigation";

const TABS: { href: Route; label: string; exact?: boolean }[] = [
  { href: "/notifications", label: "Queue", exact: true },
  { href: "/notifications/templates", label: "Templates" },
  { href: "/notifications/compose", label: "Compose" },
  { href: "/notifications/settings", label: "Email settings" },
];

export default function NotificationsLayout({ children }: { children: React.ReactNode }) {
  const pathname = usePathname();

  return (
    <div className="mx-auto max-w-6xl px-6 py-8">
      <div className="mb-5 rise-in">
        <p className="text-[10px] uppercase tracking-[0.3em] text-[color:var(--color-accent)]">
          Technical
        </p>
        <h1 className="text-3xl mt-1" style={{ fontFamily: "var(--font-display)" }}>
          Notifications<span className="caret-blink text-[color:var(--color-accent)]">▍</span>
        </h1>
        <p className="text-sm text-[color:var(--color-muted-fg)] mt-1 max-w-2xl">
          Watch the delivery queue, manage message templates, and send messages by hand.
        </p>
      </div>

      <nav
        aria-label="Notifications sections"
        className="mb-6 flex flex-wrap items-center gap-1 border-b border-[color:var(--color-border)]"
      >
        {TABS.map((tab) => {
          const active = tab.exact
            ? pathname === tab.href
            : pathname === tab.href || pathname.startsWith(tab.href + "/");
          return (
            <Link
              key={tab.href}
              href={tab.href}
              className={`relative px-3 py-2 text-sm transition-colors ${
                active
                  ? "text-[color:var(--color-accent)]"
                  : "text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-base-fg)]"
              }`}
            >
              {tab.label}
              {active ? (
                <motion.span
                  layoutId="notifications-tab"
                  transition={{ type: "spring", stiffness: 500, damping: 40 }}
                  className="absolute inset-x-1 -bottom-px h-0.5 rounded-full bg-[color:var(--color-accent)] shadow-[var(--glow-accent)]"
                />
              ) : null}
            </Link>
          );
        })}
      </nav>

      {children}
    </div>
  );
}
