"use client";

import { motion, AnimatePresence, useReducedMotion } from "framer-motion";
import { useEffect, useId, useRef } from "react";
import { createPortal } from "react-dom";

import { cn } from "@/lib/utils";

interface DialogProps {
  open: boolean;
  onClose: () => void;
  title: string;
  description?: string;
  children?: React.ReactNode;
  /** When set, clicking the backdrop will close. Defaults to true. */
  dismissOnBackdrop?: boolean;
}

export function Dialog({
  open,
  onClose,
  title,
  description,
  children,
  dismissOnBackdrop = true,
}: DialogProps) {
  const titleId = useId();
  const descriptionId = useId();
  const dialogRef = useRef<HTMLDivElement | null>(null);
  const reduceMotion = useReducedMotion();

  // Escape to close + focus management.
  useEffect(() => {
    if (!open) return;
    const previouslyFocused = document.activeElement as HTMLElement | null;
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape") {
        e.stopPropagation();
        onClose();
      }
    }
    document.addEventListener("keydown", onKey);
    // Move focus into the dialog so screen readers and keyboard users land here.
    const t = setTimeout(() => {
      const first = dialogRef.current?.querySelector<HTMLElement>(
        'button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])',
      );
      (first ?? dialogRef.current)?.focus();
    }, 0);
    return () => {
      document.removeEventListener("keydown", onKey);
      clearTimeout(t);
      previouslyFocused?.focus?.();
    };
  }, [open, onClose]);

  if (typeof window === "undefined") return null;

  return createPortal(
    <AnimatePresence>
      {open ? (
        <motion.div
          className="fixed inset-0 z-50 flex items-center justify-center px-4"
          initial={reduceMotion ? false : { opacity: 0 }}
          animate={{ opacity: 1 }}
          exit={{ opacity: 0 }}
          transition={{ duration: 0.12 }}
          aria-hidden={false}
        >
          <button
            type="button"
            aria-label="Close dialog"
            tabIndex={-1}
            onClick={dismissOnBackdrop ? onClose : undefined}
            className="absolute inset-0 bg-black/50 backdrop-blur-sm cursor-default"
          />
          <motion.div
            ref={dialogRef}
            role="dialog"
            aria-modal="true"
            aria-labelledby={titleId}
            aria-describedby={description ? descriptionId : undefined}
            tabIndex={-1}
            initial={
              reduceMotion
                ? { opacity: 0 }
                : { opacity: 0, scale: 0.97, y: 8 }
            }
            animate={{ opacity: 1, scale: 1, y: 0 }}
            exit={reduceMotion ? { opacity: 0 } : { opacity: 0, scale: 0.97, y: 4 }}
            transition={{ duration: 0.14, ease: "easeOut" }}
            className={cn(
              "relative w-full max-w-md rounded-lg border border-[color:var(--color-border)]",
              "bg-[color:var(--color-surface)] shadow-2xl",
              "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-accent)]/60",
            )}
          >
            <div className="px-5 pt-5 pb-2">
              <h2
                id={titleId}
                className="text-lg"
                style={{ fontFamily: "var(--font-display)" }}
              >
                {title}
              </h2>
              {description ? (
                <p
                  id={descriptionId}
                  className="mt-1 text-sm text-[color:var(--color-muted-fg)]"
                >
                  {description}
                </p>
              ) : null}
            </div>
            <div className="px-5 pb-5">{children}</div>
          </motion.div>
        </motion.div>
      ) : null}
    </AnimatePresence>,
    document.body,
  );
}

export function DialogActions({ children }: { children: React.ReactNode }) {
  return (
    <div className="mt-4 flex items-center justify-end gap-2">{children}</div>
  );
}
