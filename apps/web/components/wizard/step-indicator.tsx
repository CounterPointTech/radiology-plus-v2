"use client";

import { Check } from "lucide-react";

import { cn } from "@/lib/utils";

export interface WizardStep {
  index: number; // 1-based
  label: string;
}

export function StepIndicator({
  steps,
  current,
  reachable,
  onJump,
}: {
  steps: WizardStep[];
  current: number;
  /** highest 1-based step the user is allowed to navigate back to without re-doing */
  reachable: number;
  onJump?: (step: number) => void;
}) {
  return (
    <ol className="flex items-center gap-2 sm:gap-3 overflow-x-auto pb-1">
      {steps.map((s, i) => {
        const isDone = s.index < current;
        const isActive = s.index === current;
        const canJump = onJump && s.index <= reachable && s.index !== current;
        return (
          <li key={s.index} className="flex items-center gap-2 sm:gap-3 shrink-0">
            <button
              type="button"
              disabled={!canJump}
              onClick={() => canJump && onJump?.(s.index)}
              className={cn(
                "flex items-center gap-2 rounded-full px-2.5 py-1 text-xs font-medium transition-colors",
                isActive &&
                  "bg-[color:var(--color-accent)]/15 text-[color:var(--color-accent)]",
                isDone &&
                  "text-[color:var(--color-base-fg)] hover:bg-[color:var(--color-surface-2)]",
                !isActive && !isDone &&
                  "text-[color:var(--color-muted-fg)]",
                !canJump && "cursor-default",
              )}
              aria-current={isActive ? "step" : undefined}
            >
              <span
                className={cn(
                  "inline-flex items-center justify-center size-5 rounded-full border text-[10px] font-mono",
                  isActive
                    ? "border-[color:var(--color-accent)] bg-[color:var(--color-accent)] text-[color:var(--color-accent-fg)]"
                    : isDone
                      ? "border-[color:var(--color-accent)]/60 bg-[color:var(--color-accent)]/15 text-[color:var(--color-accent)]"
                      : "border-[color:var(--color-border)]",
                )}
              >
                {isDone ? <Check className="size-3" /> : s.index}
              </span>
              <span className="hidden sm:inline">{s.label}</span>
            </button>
            {i < steps.length - 1 ? (
              <span
                aria-hidden
                className={cn(
                  "h-px w-6 sm:w-10",
                  s.index < current
                    ? "bg-[color:var(--color-accent)]/60"
                    : "bg-[color:var(--color-border)]",
                )}
              />
            ) : null}
          </li>
        );
      })}
    </ol>
  );
}
