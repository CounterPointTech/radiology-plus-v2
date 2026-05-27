import { cn } from "@/lib/utils";
import type { HTMLAttributes } from "react";

type BadgeVariant = "neutral" | "accent" | "caution" | "danger" | "success";

interface BadgeProps extends HTMLAttributes<HTMLSpanElement> {
  variant?: BadgeVariant;
}

const variantClasses: Record<BadgeVariant, string> = {
  neutral:
    "bg-[color:var(--color-surface-2)] text-[color:var(--color-muted-fg)] border-[color:var(--color-border)]",
  accent:
    "bg-[color:var(--color-accent)]/15 text-[color:var(--color-accent)] border-[color:var(--color-accent)]/30",
  caution:
    "bg-[color:var(--color-caution)]/20 text-[color:var(--color-caution)] border-[color:var(--color-caution)]/40",
  danger:
    "bg-[color:var(--color-novarad-red)]/15 text-[color:var(--color-novarad-red)] border-[color:var(--color-novarad-red)]/40",
  success:
    "bg-[oklch(0.72_0.14_160)]/15 text-[oklch(0.72_0.14_160)] border-[oklch(0.72_0.14_160)]/40",
};

export function Badge({ variant = "neutral", className, ...rest }: BadgeProps) {
  return (
    <span
      className={cn(
        "inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-xs font-medium",
        variantClasses[variant],
        className,
      )}
      {...rest}
    />
  );
}
