import type { Route } from "next";

import type { NavItem } from "@/components/nav-dropdown";
import {
  canAccessAdmin,
  canAccessBilling,
  canAccessTechValidation,
  canManageTemplates,
  type Role,
} from "./types";

// Single source of truth for role-based page visibility. The top-bar nav and
// the (tech) layout's route guard both read from here, so what a user can see
// and what they can visit cannot drift apart. Server-side enforcement is
// independent (every API handler checks capabilities itself).

export function isRadiologist(role: Role | string): boolean {
  return role === "Radiologist";
}

/** Where a role lands after login and where denied navigation is sent. */
export function roleHome(role: Role | string): Route {
  return isRadiologist(role) ? "/rad" : "/validation";
}

interface RouteRule {
  prefix: string;
  allowed: (role: Role | string) => boolean;
}

// Prefix rules cover nested routes (e.g. /validation/wizard/[id]). Paths that
// match no rule are allowed through — they fall through to Next's 404.
const ROUTE_RULES: RouteRule[] = [
  { prefix: "/validation", allowed: canAccessTechValidation },
  { prefix: "/billing", allowed: canAccessBilling },
  { prefix: "/admin", allowed: canAccessAdmin },
  { prefix: "/templates", allowed: canManageTemplates },
  { prefix: "/rad", allowed: () => true },
];

export function canVisit(role: Role | string, pathname: string): boolean {
  const rule = ROUTE_RULES.find(
    (r) => pathname === r.prefix || pathname.startsWith(r.prefix + "/"),
  );
  return rule ? rule.allowed(role) : true;
}

export interface NavEntry {
  label: string;
  visible: (role: Role | string) => boolean;
  /** A plain link when href is set … */
  href?: Route;
  /** … or a dropdown when items are set. */
  items?: NavItem[];
}

export const TECH_NAV: NavEntry[] = [
  { label: "Validation", href: "/validation", visible: canAccessTechValidation },
  {
    label: "Billing",
    visible: canAccessBilling,
    items: [
      { href: "/billing/rvu", label: "CPT & RVU" },
      { href: "/billing/reconciliation", label: "Reconciliation" },
      { href: "/billing/unmapped", label: "Unmapped codes" },
    ],
  },
  {
    label: "Admin",
    visible: canAccessAdmin,
    items: [
      { href: "/admin/status-banner", label: "Status banner" },
      { href: "/admin/mmodal-connection", label: "M*Modal connection" },
      { href: "/templates", label: "Reason templates" },
    ],
  },
];
