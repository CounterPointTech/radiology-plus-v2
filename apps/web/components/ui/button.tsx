"use client";

import { forwardRef, type ButtonHTMLAttributes } from "react";

import { cn } from "@/lib/utils";

export type ButtonVariant =
  | "primary"
  | "secondary"
  | "ghost"
  | "danger"
  | "caution";
export type ButtonSize = "sm" | "md" | "lg";

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant;
  size?: ButtonSize;
  loading?: boolean;
}

const base =
  "inline-flex items-center justify-center gap-2 rounded-md font-medium select-none " +
  "transition-[background-color,color,opacity,box-shadow,transform] duration-150 " +
  "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-accent)] focus-visible:ring-offset-2 focus-visible:ring-offset-[color:var(--color-base-bg)] " +
  "disabled:opacity-50 disabled:cursor-not-allowed disabled:pointer-events-none active:translate-y-px";

const variantClasses: Record<ButtonVariant, string> = {
  primary:
    "bg-[color:var(--color-accent)] text-[color:var(--color-accent-fg)] hover:brightness-110 shadow-sm",
  secondary:
    "border border-[color:var(--color-border)] bg-[color:var(--color-surface)] hover:bg-[color:var(--color-surface-2)]",
  ghost:
    "text-[color:var(--color-base-fg)] hover:bg-[color:var(--color-surface)]",
  danger:
    "bg-[color:var(--color-novarad-red)] text-white hover:brightness-110",
  caution:
    "bg-[color:var(--color-caution)] text-[oklch(0.18_0.04_25)] hover:brightness-110",
};

const sizeClasses: Record<ButtonSize, string> = {
  sm: "h-8 px-3 text-sm",
  md: "h-10 px-4 text-sm",
  lg: "h-12 px-6 text-base",
};

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button(
  {
    variant = "primary",
    size = "md",
    loading = false,
    disabled,
    className,
    children,
    type = "button",
    ...rest
  },
  ref,
) {
  return (
    <button
      ref={ref}
      type={type}
      disabled={disabled || loading}
      className={cn(base, variantClasses[variant], sizeClasses[size], className)}
      {...rest}
    >
      {loading ? (
        <span
          aria-hidden
          className="size-4 rounded-full border-2 border-current border-r-transparent animate-spin"
        />
      ) : null}
      {children}
    </button>
  );
});
