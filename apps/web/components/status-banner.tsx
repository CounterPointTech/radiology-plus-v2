"use client";

import { useQuery } from "@tanstack/react-query";
import { AlertCircle, AlertTriangle, Info, Wrench, X } from "lucide-react";
import { useEffect, useRef, useState } from "react";

import { adminApi } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { BannerSeverity } from "@/lib/types";
import { cn } from "@/lib/utils";

// 45s sits in the requested 30-60s window, so a freshly posted outage notice reaches
// users already in the app within one interval. refetchIntervalInBackground keeps it
// current even for an idle/backgrounded tab (the app disables refetchOnWindowFocus).
const POLL_MS = 45_000;

type SeverityStyle = { bar: string; icon: typeof Info };

// Severity to color. A status banner is a sanctioned alert context, so each level gets a
// solid, saturated bar: blue=info, slate=maintenance, yellow=warning, red=critical.
// Yellow takes dark text; the rest take white. (Red/amber here is the sanctioned alert
// exception; cyan accent stays the default non-error color everywhere else.)
const SEVERITY: Record<number, SeverityStyle> = {
  [BannerSeverity.Info]: {
    bar: "bg-[oklch(0.55_0.15_250)] text-white border-[oklch(0.46_0.15_250)]",
    icon: Info,
  },
  [BannerSeverity.Maintenance]: {
    bar: "bg-[oklch(0.46_0.03_255)] text-white border-[oklch(0.38_0.03_255)]",
    icon: Wrench,
  },
  [BannerSeverity.Warning]: {
    bar: "bg-[oklch(0.86_0.17_95)] text-[oklch(0.27_0.05_95)] border-[oklch(0.77_0.17_95)]",
    icon: AlertTriangle,
  },
  [BannerSeverity.Critical]: {
    bar: "bg-[oklch(0.55_0.20_25)] text-white border-[oklch(0.46_0.20_25)]",
    icon: AlertCircle,
  },
};

/**
 * Presentational status bar. Shared by the live poller and the admin preview so they
 * always render identically. The pulse on the leading icon is the "animated" affordance;
 * the global `prefers-reduced-motion` rule in globals.css auto-dampens it.
 */
export function BannerView({
  message,
  severity,
  isAnimated,
  speed = 5,
  onDismiss,
}: {
  message: string;
  severity: number;
  isAnimated: boolean;
  speed?: number;
  onDismiss?: () => void;
}) {
  const style = SEVERITY[severity] ?? SEVERITY[BannerSeverity.Info];
  const Icon = style.icon;
  // Critical outages are announced assertively so a screen-reader user hears them
  // mid-task; lower severities stay polite.
  const critical = severity >= BannerSeverity.Critical;
  const text = message || "Your banner message will appear here.";

  // Drive the marquee at a constant pixels-per-second so the narrow preview and the
  // full-width live bar scroll at the same visual speed. The CSS animates translateX by
  // the element's own width (container width via padding-left:100% + text), so the travel
  // distance equals that width; duration = width / pxPerSec. A ResizeObserver keeps it
  // correct as the bar width or message length change.
  const marqueeRef = useRef<HTMLSpanElement>(null);
  const [durationS, setDurationS] = useState<number | null>(null);
  useEffect(() => {
    if (!isAnimated) return;
    const el = marqueeRef.current;
    if (!el) return;
    const clamped = Math.min(10, Math.max(1, Math.round(speed)));
    const pxPerSec = 20 + (clamped - 1) * 20; // speed 1..10 -> 20..200 px/s
    const recompute = () => {
      const travel = el.offsetWidth; // padding-left(=container width) + text width
      setDurationS(travel > 0 ? Math.max(3, travel / pxPerSec) : null);
    };
    recompute();
    const ro = new ResizeObserver(recompute);
    ro.observe(el);
    return () => ro.disconnect();
  }, [isAnimated, speed]);

  return (
    <div
      role={critical ? "alert" : "status"}
      aria-live={critical ? "assertive" : "polite"}
      className={cn(
        "w-full border-b px-6 py-2.5 flex items-center gap-3 text-sm",
        style.bar,
      )}
    >
      <Icon className="size-4 shrink-0" aria-hidden />
      {isAnimated ? (
        // Marquee: the message scrolls right-to-left at a constant bar color. Falls back
        // to a static line under prefers-reduced-motion (see globals.css).
        <div className="flex-1 overflow-hidden">
          <span
            ref={marqueeRef}
            className="banner-marquee inline-block whitespace-nowrap font-medium"
            style={durationS ? { animationDuration: `${durationS}s` } : undefined}
          >
            {text}
          </span>
        </div>
      ) : (
        <span className="flex-1 font-medium break-words">{text}</span>
      )}
      {onDismiss ? (
        <button
          type="button"
          onClick={onDismiss}
          className="shrink-0 opacity-70 hover:opacity-100 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-current rounded"
          aria-label="Dismiss notice"
        >
          <X className="size-4" />
        </button>
      ) : null}
    </div>
  );
}

/**
 * Live status banner mounted in the authenticated shell. Polls the active-banner
 * endpoint and renders nothing when there is no active banner. Only mounted inside the
 * (tech) shell, so it never appears on the login page.
 */
export function StatusBanner() {
  const { isAuthenticated, isHydrated } = useAuth();
  const [dismissedId, setDismissedId] = useState<number | null>(null);

  const { data: banner } = useQuery({
    queryKey: ["active-banner"],
    queryFn: () => adminApi.activeBanner(),
    enabled: isHydrated && isAuthenticated,
    refetchInterval: POLL_MS,
    refetchIntervalInBackground: true,
  });

  if (!banner) return null;
  if (banner.isDismissible && dismissedId === banner.bannerId) return null;

  return (
    <BannerView
      message={banner.message}
      severity={banner.severity}
      isAnimated={banner.isAnimated}
      speed={banner.marqueeSpeed}
      onDismiss={
        banner.isDismissible ? () => setDismissedId(banner.bannerId) : undefined
      }
    />
  );
}
