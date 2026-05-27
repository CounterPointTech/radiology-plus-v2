"use client";

import { CheckCircle2, XCircle } from "lucide-react";
import Link from "next/link";

import { CookingProgress } from "@/components/cooking-progress";
import { Button } from "@/components/ui/button";
import { Card, CardBody, CardHeader, CardSubtitle, CardTitle } from "@/components/ui/card";
import { Spinner } from "@/components/ui/spinner";
import type { DoTheDoOutcome } from "@/lib/types";

interface StepDoTheDoProps {
  validationId: string;
  finalizing: boolean;
  outcome: DoTheDoOutcome | null;
  errorMessage: string | null;
  onStart: () => void;
  onRetry: () => void;
  onBack: () => void;
}

export function StepDoTheDo({
  validationId,
  finalizing,
  outcome,
  errorMessage,
  onStart,
  onRetry,
  onBack,
}: StepDoTheDoProps) {
  const succeeded = !!outcome?.success;
  const failed = (outcome && !outcome.success) || !!errorMessage;
  // Allow stepping back to Review until the write has actually succeeded.
  const canGoBack = !finalizing && !succeeded;

  return (
    <Card>
      <CardHeader>
        <CardTitle>Finalize</CardTitle>
        <CardSubtitle>
          Apply your changes to the patient record and order. You&apos;ll see each
          update complete in real time.
        </CardSubtitle>
      </CardHeader>
      <CardBody className="space-y-5">
        {!finalizing && !outcome ? (
          <div className="rounded-md border border-dashed border-[color:var(--color-border)] px-4 py-6 text-center">
            <p className="text-sm text-[color:var(--color-muted-fg)] mb-3">
              Ready when you are.
            </p>
            <Button size="lg" onClick={onStart}>
              Finalize
            </Button>
          </div>
        ) : (
          <CookingProgress
            validationId={validationId}
            active={finalizing && !outcome}
            outcome={outcome}
            errorMessage={errorMessage}
          />
        )}

        {finalizing && !outcome ? (
          <div className="flex items-center gap-2 text-xs text-[color:var(--color-muted-fg)]">
            <Spinner size={14} />
            Applying your changes…
          </div>
        ) : null}

        {succeeded ? (
          <div className="rounded-md border border-[oklch(0.72_0.14_160)]/40 bg-[oklch(0.72_0.14_160)]/10 px-4 py-3 flex items-start gap-3">
            <CheckCircle2 className="size-5 text-[oklch(0.72_0.14_160)] mt-0.5" />
            <div className="space-y-1 flex-1">
              <p className="text-sm font-medium">
                All {outcome!.completedSteps}/{outcome!.totalSteps} steps succeeded.
              </p>
              <p className="text-xs text-[color:var(--color-muted-fg)]">
                Validation is marked Completed.
              </p>
            </div>
            <Link
              href="/validation"
              className="inline-flex items-center justify-center h-8 px-3 text-sm rounded-md bg-[color:var(--color-accent)] text-[color:var(--color-accent-fg)] font-medium hover:brightness-110"
            >
              Back to worklist
            </Link>
          </div>
        ) : null}

        {failed ? (
          <div className="rounded-md border border-[color:var(--color-novarad-red)]/40 bg-[color:var(--color-novarad-red)]/10 px-4 py-3 flex items-start gap-3">
            <XCircle className="size-5 text-[color:var(--color-novarad-red)] mt-0.5" />
            <div className="space-y-1 flex-1">
              <p className="text-sm font-medium text-[color:var(--color-novarad-red)]">
                Finalize failed.
              </p>
              <p className="text-xs text-[color:var(--color-muted-fg)]">
                {outcome?.failureMessage ?? errorMessage ?? "Unknown failure."} ·{" "}
                Completed {outcome?.completedSteps ?? 0}/{outcome?.totalSteps ?? "?"} steps.
              </p>
            </div>
            <Button size="sm" variant="secondary" onClick={onRetry}>
              Try again
            </Button>
          </div>
        ) : null}
      </CardBody>
      {canGoBack ? (
        <div className="px-5 py-3 border-t border-[color:var(--color-border)] flex items-center justify-start">
          <Button variant="ghost" onClick={onBack}>
            Back
          </Button>
        </div>
      ) : null}
    </Card>
  );
}
