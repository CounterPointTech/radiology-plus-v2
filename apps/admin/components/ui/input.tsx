"use client";

import { forwardRef, type InputHTMLAttributes, type TextareaHTMLAttributes } from "react";

import { cn } from "@/lib/utils";

const fieldBase =
  "block w-full rounded-md border border-[color:var(--color-border)] bg-[color:var(--color-surface)] " +
  "px-3 py-2 text-sm placeholder:text-[color:var(--color-muted-fg)] " +
  "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-accent)]/60 focus-visible:border-[color:var(--color-accent)] " +
  "disabled:opacity-50 disabled:cursor-not-allowed";

export const Input = forwardRef<HTMLInputElement, InputHTMLAttributes<HTMLInputElement>>(
  function Input({ className, ...rest }, ref) {
    return <input ref={ref} className={cn(fieldBase, "h-10", className)} {...rest} />;
  },
);

export const Textarea = forwardRef<
  HTMLTextAreaElement,
  TextareaHTMLAttributes<HTMLTextAreaElement>
>(function Textarea({ className, ...rest }, ref) {
  return (
    <textarea
      ref={ref}
      className={cn(fieldBase, "min-h-[6rem] resize-y", className)}
      {...rest}
    />
  );
});

interface LabelProps extends React.LabelHTMLAttributes<HTMLLabelElement> {
  required?: boolean;
}

export function Label({ required, children, className, ...rest }: LabelProps) {
  return (
    <label
      className={cn(
        "text-xs font-medium uppercase tracking-[0.12em] text-[color:var(--color-muted-fg)]",
        className,
      )}
      {...rest}
    >
      {children}
      {required ? (
        <span aria-hidden className="text-[color:var(--color-caution)]">
          {" "}
          *
        </span>
      ) : null}
    </label>
  );
}
