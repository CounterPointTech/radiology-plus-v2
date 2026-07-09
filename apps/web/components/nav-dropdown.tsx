"use client";

import type { Route } from "next";
import { ChevronDown } from "lucide-react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useEffect, useId, useRef, useState } from "react";

export interface NavItem {
  href: Route;
  label: string;
}

// Shared link styling: cyan accent when the route is active, muted otherwise.
const activeCls = "text-[color:var(--color-accent)] bg-[color:var(--color-accent)]/10";
const idleCls =
  "text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-base-fg)] hover:bg-[color:var(--color-surface-2)]";

// startsWith so child routes (e.g. /validation/wizard/…) still light their parent.
function isRouteActive(pathname: string, href: string): boolean {
  return pathname === href || pathname.startsWith(href + "/");
}

/** A plain top-level nav link with active-route highlighting. */
export function NavLink({ href, label }: NavItem) {
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

interface NavDropdownProps {
  label: string;
  items: NavItem[];
}

/**
 * A categorized nav menu: a trigger button that opens a popover list of links.
 * Closes on outside click, Escape, route change, or selecting an item. The
 * trigger is highlighted whenever the current route belongs to this category.
 */
export function NavDropdown({ label, items }: NavDropdownProps) {
  const pathname = usePathname();
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const menuId = useId();

  const groupActive = items.some((it) => isRouteActive(pathname, it.href));

  useEffect(() => {
    if (!open) return;
    function onPointer(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    }
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape") setOpen(false);
    }
    document.addEventListener("mousedown", onPointer);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onPointer);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  // Close when the route changes (a link was followed).
  useEffect(() => {
    setOpen(false);
  }, [pathname]);

  return (
    <div ref={ref} className="relative">
      <button
        type="button"
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={menuId}
        onClick={() => setOpen((v) => !v)}
        className={`flex items-center gap-1 px-3 py-1.5 rounded-md ${
          groupActive || open ? activeCls : idleCls
        }`}
      >
        {label}
        <ChevronDown
          className={`size-3.5 transition-transform ${open ? "rotate-180" : ""}`}
        />
      </button>
      {open ? (
        <div
          id={menuId}
          role="menu"
          className="absolute left-0 mt-1 min-w-48 rounded-md border border-[color:var(--color-border)] bg-[color:var(--color-surface)] shadow-lg p-1 z-40"
        >
          {items.map((it) => {
            const active = isRouteActive(pathname, it.href);
            return (
              <Link
                key={it.href}
                href={it.href}
                role="menuitem"
                onClick={() => setOpen(false)}
                className={`block px-3 py-1.5 rounded-md text-sm ${active ? activeCls : idleCls}`}
              >
                {it.label}
              </Link>
            );
          })}
        </div>
      ) : null}
    </div>
  );
}
