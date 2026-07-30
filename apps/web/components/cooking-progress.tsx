"use client";

import { animated, useSpring, useSprings } from "react-spring";
import { CheckCircle2, Loader2, XCircle } from "lucide-react";
import { useEffect, useMemo, useRef, useState } from "react";

import { buildMonitoringConnection, safeStop } from "@/lib/signalr-client";
import {
  DoTheDoRunStatus,
  type DoTheDoOutcome,
  type DoTheDoProgressEvent,
} from "@/lib/types";
import { cn } from "@/lib/utils";

interface CookingStep {
  index: number;
  stepKey: string;
  description: string;
  status: number; // DoTheDoRunStatus, or 0 = pending
  errorMessage: string | null;
}

interface CookingProgressProps {
  validationId: string;
  active: boolean;
  /** Render the card. The realtime connection is established on mount regardless,
   *  so we're already joined to the validation group before Finalize is clicked —
   *  otherwise the first progress events land before negotiation finishes and the
   *  run appears to jump straight to "Cooked!". */
  visible?: boolean;
  /** Final result from the finalize call. Makes the heading authoritative even
   *  if SignalR progress events were missed. */
  outcome?: DoTheDoOutcome | null;
  errorMessage?: string | null;
  /** Fired once on the final Succeeded event (stepIndex === stepCount). */
  onComplete?: (event: DoTheDoProgressEvent) => void;
  /** Fired on any Failed event. */
  onError?: (event: DoTheDoProgressEvent) => void;
}

function prefersReducedMotion(): boolean {
  if (typeof window === "undefined") return false;
  return window.matchMedia("(prefers-reduced-motion: reduce)").matches;
}

export function CookingProgress({
  validationId,
  active,
  visible = true,
  outcome,
  errorMessage,
  onComplete,
  onError,
}: CookingProgressProps) {
  const [steps, setSteps] = useState<CookingStep[]>([]);
  const [reduceMotion, setReduceMotion] = useState(false);
  const dingPlayedRef = useRef(false);

  useEffect(() => {
    setReduceMotion(prefersReducedMotion());
  }, []);

  // Hold the callbacks in refs so a parent re-render with fresh closures can't
  // tear down and rebuild the connection mid-run.
  const onCompleteRef = useRef(onComplete);
  const onErrorRef = useRef(onError);
  useEffect(() => {
    onCompleteRef.current = onComplete;
    onErrorRef.current = onError;
  }, [onComplete, onError]);

  useEffect(() => {
    let cancelled = false;

    const connection = buildMonitoringConnection();
    connection.on("DoTheDoProgress", (event: DoTheDoProgressEvent) => {
      if (cancelled) return;
      setSteps((current) => mergeProgress(current, event));
      if (
        event.status === DoTheDoRunStatus.Succeeded &&
        event.stepIndex === event.stepCount
      ) {
        if (!dingPlayedRef.current) {
          dingPlayedRef.current = true;
          playDing();
        }
        onCompleteRef.current?.(event);
      }
      if (event.status === DoTheDoRunStatus.Failed) {
        onErrorRef.current?.(event);
      }
    });

    (async () => {
      try {
        await connection.start();
        if (!cancelled) {
          await connection.invoke("JoinValidationAsync", validationId);
        }
      } catch {
        // Swallow — the orchestrator will still return its final outcome via /finalize.
      }
    })();

    return () => {
      cancelled = true;
      (async () => {
        try {
          await connection.invoke("LeaveValidationAsync", validationId);
        } catch {
          // ignore
        }
        await safeStop(connection);
      })();
    };
  }, [validationId]);

  // Reset playback gate when we re-arm.
  useEffect(() => {
    if (active) {
      dingPlayedRef.current = false;
      setSteps([]);
    }
  }, [active]);

  const failed =
    !!errorMessage ||
    (outcome ? !outcome.success : false) ||
    steps.some((s) => s.status === DoTheDoRunStatus.Failed);
  const cooked =
    !failed &&
    ((outcome?.success ?? false) ||
      (steps.length > 0 && steps.every((s) => s.status === DoTheDoRunStatus.Succeeded)));
  const cooking = !failed && !cooked;

  // Stay mounted (and connected) but render nothing until the run starts.
  if (!visible) return null;

  const heading = failed ? "Couldn’t finish" : cooked ? "Cooked!" : "Cooking…";
  const subtext = failed
    ? "Some updates didn’t apply — see below."
    : cooked
      ? "All updates applied."
      : "Applying your changes to the patient record and order.";

  return (
    <div className="rounded-lg border border-[color:var(--color-border)] bg-[color:var(--color-surface)] overflow-hidden">
      <div className="px-5 py-4 border-b border-[color:var(--color-border)] flex items-center gap-3">
        <Pot reduceMotion={reduceMotion} active={active && cooking} />
        <div>
          <h3
            className="text-lg"
            style={{ fontFamily: "var(--font-display)" }}
          >
            {heading}
          </h3>
          <p className="text-xs text-[color:var(--color-muted-fg)]">
            {subtext}
          </p>
        </div>
      </div>
      <ol className="divide-y divide-[color:var(--color-border)]">
        {steps.length === 0 ? (
          <li className="px-5 py-6 text-sm text-[color:var(--color-muted-fg)]">
            Starting…
          </li>
        ) : (
          steps.map((s) => <StepLine key={s.index} step={s} />)
        )}
      </ol>
    </div>
  );
}

function StepLine({ step }: { step: CookingStep }) {
  const styles = useSpring({
    from: { opacity: 0, transform: "translateY(4px)" },
    to: { opacity: 1, transform: "translateY(0px)" },
    config: { tension: 220, friction: 22 },
  });

  let icon: React.ReactNode;
  let tone = "text-[color:var(--color-muted-fg)]";
  if (step.status === DoTheDoRunStatus.Succeeded) {
    icon = <CheckCircle2 className="size-5 text-[oklch(0.72_0.14_160)]" />;
    tone = "text-[color:var(--color-base-fg)]";
  } else if (step.status === DoTheDoRunStatus.Failed) {
    icon = <XCircle className="size-5 text-[color:var(--color-novarad-red)]" />;
    tone = "text-[color:var(--color-novarad-red)]";
  } else if (step.status === DoTheDoRunStatus.Started) {
    icon = <Loader2 className="size-5 animate-spin text-[color:var(--color-accent)]" />;
    tone = "text-[color:var(--color-base-fg)]";
  } else {
    icon = <span className="inline-block size-2 rounded-full bg-[color:var(--color-border)]" />;
  }

  return (
    <animated.li style={styles} className="px-5 py-3 flex items-center gap-3">
      <span className="w-6 flex justify-center">{icon}</span>
      <div className={cn("flex-1 min-w-0", tone)}>
        <div className="text-sm font-medium truncate">{step.description}</div>
        {step.errorMessage ? (
          <p className="mt-1 text-xs text-[color:var(--color-novarad-red)]">
            {step.errorMessage}
          </p>
        ) : null}
      </div>
    </animated.li>
  );
}

function Pot({ reduceMotion, active }: { reduceMotion: boolean; active: boolean }) {
  // Three bubbles rising and fading. Anchored to the rim of the pot.
  const bubbleCount = 3;
  const [springs] = useSprings(
    bubbleCount,
    (i) =>
      reduceMotion || !active
        ? {
            from: { translateY: 0, scale: 1, opacity: 0.6 },
            to: { translateY: 0, scale: 1, opacity: 0.6 },
          }
        : {
            from: { translateY: 0, scale: 0.6, opacity: 0 },
            to: async (next: (v: object) => Promise<void>) => {
              while (true) {
                await next({ translateY: -10, scale: 1, opacity: 1 });
                await next({ translateY: -22, scale: 1.05, opacity: 0 });
                await next({ translateY: 0, scale: 0.5, opacity: 0 });
              }
            },
            delay: i * 220,
            config: { tension: 120, friction: 14 },
          },
    [reduceMotion, active],
  );

  return (
    <div
      aria-hidden
      className="relative w-12 h-12 flex items-end justify-center"
    >
      {springs.map((style, i) => (
        <animated.span
          key={i}
          style={{
            ...style,
            left: `${30 + i * 16}%`,
          }}
          className="absolute bottom-7 size-1.5 rounded-full bg-[color:var(--color-accent)]"
        />
      ))}
      {/* Pot body */}
      <div className="relative w-10 h-7 rounded-b-lg rounded-t-sm bg-[color:var(--color-surface-2)] border border-[color:var(--color-border)] overflow-hidden">
        <div className="absolute inset-x-0 top-1 h-1.5 bg-[color:var(--color-accent)]/70 rounded-full mx-1" />
      </div>
    </div>
  );
}

function mergeProgress(
  current: CookingStep[],
  event: DoTheDoProgressEvent,
): CookingStep[] {
  const next: CookingStep[] = [];
  // Pre-fill placeholders up to stepCount once we know it.
  for (let i = 1; i <= event.stepCount; i++) {
    const existing = current.find((s) => s.index === i);
    if (existing) {
      next.push(existing);
    } else {
      next.push({
        index: i,
        stepKey: i === event.stepIndex ? event.stepKey : "",
        description: i === event.stepIndex ? event.description : "Pending…",
        status: 0,
        errorMessage: null,
      });
    }
  }
  const target = next.find((s) => s.index === event.stepIndex);
  if (target) {
    target.stepKey = event.stepKey;
    target.description = event.description;
    target.status = event.status;
    target.errorMessage = event.errorMessage;
  }
  return next;
}

function playDing(): void {
  if (typeof window === "undefined") return;
  if (prefersReducedMotion()) return;
  try {
    const audio = new Audio("/ding.mp3");
    audio.volume = 0.6;
    void audio.play();
  } catch {
    // Autoplay can be blocked; silent fallback.
  }
}
