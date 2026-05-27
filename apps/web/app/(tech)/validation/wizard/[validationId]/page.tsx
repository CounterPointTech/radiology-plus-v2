"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AxiosError } from "axios";
import { AnimatePresence, motion, useReducedMotion } from "framer-motion";
import { ArrowLeft } from "lucide-react";
import Link from "next/link";
import { useParams, useRouter } from "next/navigation";
import { useEffect, useMemo, useRef, useState } from "react";

import { Button } from "@/components/ui/button";
import { Dialog, DialogActions } from "@/components/ui/dialog";
import { Spinner } from "@/components/ui/spinner";
import { StepDoTheDo } from "@/components/wizard/step-do-the-do";
import { StepIndicator } from "@/components/wizard/step-indicator";
import { StepOrder } from "@/components/wizard/step-order";
import { StepReason } from "@/components/wizard/step-reason";
import { StepReview } from "@/components/wizard/step-review";
import { StepStudy, type CorrectionDraft } from "@/components/wizard/step-study";
import { techApi } from "@/lib/api";
import {
  PatientAction,
  ValidationStatus,
  type CandidateOrder,
  type DoTheDoOutcome,
  type PatientSearchResult,
  type ReadyStudy,
  type StepRequest,
  type ValidationRecord,
} from "@/lib/types";

const EMPTY_CORRECTION: CorrectionDraft = {
  lastName: "",
  firstName: "",
  middleName: "",
  birthDate: "",
  gender: "",
};

const STEPS = [
  { index: 1, label: "Study" },
  { index: 2, label: "Order" },
  { index: 3, label: "Reason" },
  { index: 4, label: "Review" },
  { index: 5, label: "Finalize" },
];

export default function WizardPage() {
  const params = useParams<{ validationId: string }>();
  const router = useRouter();
  const queryClient = useQueryClient();
  const reduceMotion = useReducedMotion();

  const validationId = params.validationId;

  const validationQuery = useQuery({
    queryKey: ["validation", validationId],
    queryFn: () => techApi.get(validationId),
    enabled: !!validationId,
  });

  const worklistQuery = useQuery({
    queryKey: ["worklist"],
    queryFn: () => techApi.worklist(),
    refetchInterval: false,
  });

  const validation = validationQuery.data;
  const studyForValidation: ReadyStudy | null = useMemo(() => {
    if (!validation || !worklistQuery.data) return null;
    return (
      worklistQuery.data.find(
        (r) => r.novaradStudyId === validation.novaradStudyId,
      ) ?? null
    );
  }, [validation, worklistQuery.data]);

  const ordersQuery = useQuery({
    queryKey: ["orders", studyForValidation?.patientPid],
    queryFn: () => techApi.ordersByPatient(studyForValidation!.patientPid!),
    enabled: !!studyForValidation?.patientPid,
  });

  // Local draft mirrors server state. Hydrates from the validation record.
  const [draft, setDraft] = useState<{
    novaradOrderId: number | null;
    reason: string;
    techNotes: string;
    createOrderRequested: boolean;
    patientAction: number;
    correction: CorrectionDraft;
    reassignTargetPatientId: number | null;
    reassignTarget: PatientSearchResult | null;
  } | null>(null);

  useEffect(() => {
    if (validation && draft === null) {
      setDraft({
        novaradOrderId: validation.novaradOrderId,
        reason: validation.reason ?? "",
        techNotes: validation.techNotes ?? "",
        createOrderRequested: validation.createOrderRequested,
        patientAction: validation.patientAction ?? PatientAction.None,
        correction: {
          lastName: validation.correction?.lastName ?? "",
          firstName: validation.correction?.firstName ?? "",
          middleName: validation.correction?.middleName ?? "",
          birthDate: validation.correction?.birthDate ?? "",
          gender: validation.correction?.gender ?? "",
        },
        reassignTargetPatientId: validation.reassignTargetPatientId,
        reassignTarget: null,
      });
    }
  }, [validation, draft]);

  const [step, setStep] = useState<1 | 2 | 3 | 4 | 5>(1);
  useEffect(() => {
    if (validation && step === 1 && validation.currentStep > 1) {
      const next = Math.min(5, Math.max(1, validation.currentStep)) as 1 | 2 | 3 | 4 | 5;
      setStep(next);
    }
  }, [validation, step]);

  const stepMutation = useMutation({
    mutationFn: async (input: { target: 1 | 2 | 3 | 4 | 5; body: StepRequest }) => {
      return techApi.step(validationId, input.target, input.body);
    },
    onSuccess: (record) => {
      queryClient.setQueryData(["validation", validationId], record);
    },
  });

  const cancelMutation = useMutation({
    mutationFn: () => techApi.cancel(validationId),
    onSuccess: () => router.push("/validation"),
  });

  const [finalizing, setFinalizing] = useState(false);
  const [outcome, setOutcome] = useState<DoTheDoOutcome | null>(null);
  const [finalizeError, setFinalizeError] = useState<string | null>(null);
  const [confirmCancel, setConfirmCancel] = useState(false);

  const finalizeMutation = useMutation({
    mutationFn: () => techApi.finalize(validationId),
    onMutate: () => {
      setOutcome(null);
      setFinalizeError(null);
      setFinalizing(true);
    },
    onSuccess: (result) => {
      setOutcome(result);
      setFinalizing(false);
      queryClient.invalidateQueries({ queryKey: ["validation", validationId] });
      queryClient.invalidateQueries({ queryKey: ["worklist"] });
    },
    onError: (err) => {
      setFinalizing(false);
      const ax = err as AxiosError<DoTheDoOutcome | { message?: string }>;
      if (ax.response?.data && "success" in (ax.response.data as object)) {
        setOutcome(ax.response!.data as DoTheDoOutcome);
      } else {
        setFinalizeError(
          (ax.response?.data as { message?: string } | undefined)?.message ??
            "Couldn't reach the orchestrator.",
        );
      }
    },
  });

  // ---- step handlers ----

  async function persistAndAdvance(
    target: 1 | 2 | 3 | 4 | 5,
    next: 1 | 2 | 3 | 4 | 5,
    body: StepRequest,
  ) {
    try {
      await stepMutation.mutateAsync({ target, body });
      setStep(next);
    } catch {
      // surfaced via stepMutation.error below
    }
  }

  const selectedOrder: CandidateOrder | null = useMemo(() => {
    if (!draft?.novaradOrderId) return null;
    return ordersQuery.data?.find((o) => o.orderId === draft.novaradOrderId) ?? null;
  }, [draft, ordersQuery.data]);

  if (validationQuery.isLoading || !validation || !draft) {
    return (
      <div className="min-h-[60vh] flex items-center justify-center">
        <Spinner size={28} />
      </div>
    );
  }

  if (validationQuery.isError) {
    return (
      <div className="mx-auto max-w-3xl px-6 py-10">
        <p className="text-sm text-[color:var(--color-novarad-red)]">
          Couldn&apos;t load this validation.
        </p>
        <Link href="/validation" className="text-sm underline mt-2 inline-block">
          Back to worklist
        </Link>
      </div>
    );
  }

  const isTerminal =
    validation.status === ValidationStatus.Completed ||
    validation.status === ValidationStatus.Failed ||
    validation.status === ValidationStatus.Cancelled;

  return (
    <div className="mx-auto max-w-3xl px-6 py-8">
      <div className="flex items-center gap-3 mb-3">
        <Link
          href="/validation"
          className="inline-flex items-center gap-1.5 text-sm text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-base-fg)]"
        >
          <ArrowLeft className="size-4" />
          Worklist
        </Link>
        <span className="text-[color:var(--color-muted-fg)]">·</span>
        <span className="text-xs font-mono text-[color:var(--color-muted-fg)]">
          {validationId.slice(0, 8)}…
        </span>
      </div>

      <div className="flex flex-wrap items-end justify-between gap-3 mb-5">
        <div>
          <p className="text-[10px] uppercase tracking-[0.3em] text-[color:var(--color-accent)]">
            Tech Validation Wizard
          </p>
          <h1
            className="text-2xl mt-1"
            style={{ fontFamily: "var(--font-display)" }}
          >
            Step {step} of 5 · {STEPS[step - 1].label}
          </h1>
        </div>
        {!isTerminal ? (
          <Button
            variant="ghost"
            size="sm"
            onClick={() => setConfirmCancel(true)}
            loading={cancelMutation.isPending}
          >
            Cancel
          </Button>
        ) : null}
      </div>

      <div className="mb-6">
        <StepIndicator
          steps={STEPS}
          current={step}
          reachable={Math.max(step, validation.currentStep)}
          onJump={(target) => {
            if (!isTerminal) setStep(target as 1 | 2 | 3 | 4 | 5);
          }}
        />
      </div>

      <AnimatePresence mode="wait">
        <motion.div
          key={step}
          initial={reduceMotion ? false : { opacity: 0, x: 24 }}
          animate={reduceMotion ? { opacity: 1 } : { opacity: 1, x: 0 }}
          exit={reduceMotion ? { opacity: 0 } : { opacity: 0, x: -24 }}
          transition={{ duration: 0.18 }}
        >
          {step === 1 ? (
            <StepStudy
              study={studyForValidation}
              patientAction={draft.patientAction}
              correction={draft.correction}
              reassignTarget={draft.reassignTarget}
              onChangeAction={(a) =>
                setDraft((d) => {
                  if (!d) return d;
                  const next = { ...d, patientAction: a };
                  // Drop a stale reassignment target if the tech leaves reassign mode.
                  if (a !== PatientAction.Reassign) {
                    next.reassignTarget = null;
                    next.reassignTargetPatientId = null;
                  }
                  if (a === PatientAction.EditInPlace) {
                    // Pre-fill from what we know so the tech edits rather than retypes.
                    // (Middle name / sex aren't on the worklist row.)
                    const empty =
                      !d.correction.lastName &&
                      !d.correction.firstName &&
                      !d.correction.birthDate;
                    if (empty && studyForValidation) {
                      next.correction = {
                        lastName: studyForValidation.patientLastName ?? "",
                        firstName: studyForValidation.patientFirstName ?? "",
                        middleName: "",
                        birthDate: studyForValidation.patientBirthDate ?? "",
                        gender: studyForValidation.patientGender ?? "",
                      };
                    }
                  } else {
                    next.correction = EMPTY_CORRECTION;
                  }
                  return next;
                })
              }
              onChangeCorrection={(c) => setDraft({ ...draft, correction: c })}
              onChangeReassignTarget={(t) =>
                setDraft({
                  ...draft,
                  reassignTarget: t,
                  reassignTargetPatientId: t?.novaradPatientId ?? null,
                })
              }
              saving={stepMutation.isPending}
              onContinue={() => {
                const nz = (s: string) => (s.trim() ? s.trim() : null);
                const body: StepRequest = {
                  patientAction: draft.patientAction,
                  patientCorrection:
                    draft.patientAction === PatientAction.EditInPlace
                      ? {
                          lastName: nz(draft.correction.lastName),
                          firstName: nz(draft.correction.firstName),
                          middleName: nz(draft.correction.middleName),
                          birthDate: nz(draft.correction.birthDate),
                          gender: nz(draft.correction.gender),
                        }
                      : null,
                  reassignTargetPatientId:
                    draft.patientAction === PatientAction.Reassign
                      ? draft.reassignTargetPatientId
                      : null,
                };
                persistAndAdvance(1, 2, body);
              }}
            />
          ) : null}

          {step === 2 ? (
            <StepOrder
              patientPid={studyForValidation?.patientPid ?? null}
              selectedOrderId={draft.novaradOrderId}
              createOrderRequested={draft.createOrderRequested}
              onSelect={(id) => setDraft({ ...draft, novaradOrderId: id })}
              onToggleCreate={(v) => setDraft({ ...draft, createOrderRequested: v })}
              onBack={() => setStep(1)}
              saving={stepMutation.isPending}
              onContinue={() =>
                persistAndAdvance(2, 3, {
                  novaradOrderId: draft.novaradOrderId,
                  createOrderRequested: draft.createOrderRequested,
                })
              }
            />
          ) : null}

          {step === 3 ? (
            <StepReason
              reason={draft.reason}
              techNotes={draft.techNotes}
              onChange={(next) => setDraft({ ...draft, ...next })}
              onBack={() => setStep(2)}
              saving={stepMutation.isPending}
              onContinue={() =>
                persistAndAdvance(3, 4, {
                  reason: draft.reason,
                  techNotes: draft.techNotes,
                })
              }
            />
          ) : null}

          {step === 4 ? (
            <StepReview
              study={studyForValidation}
              order={selectedOrder}
              reason={draft.reason}
              techNotes={draft.techNotes}
              createOrderRequested={draft.createOrderRequested}
              patientAction={draft.patientAction}
              correction={draft.correction}
              reassignTarget={draft.reassignTarget}
              reassignTargetPatientId={draft.reassignTargetPatientId}
              onBack={() => setStep(3)}
              saving={stepMutation.isPending}
              onContinue={() => persistAndAdvance(4, 5, {})}
            />
          ) : null}

          {step === 5 ? (
            <StepDoTheDo
              validationId={validationId}
              finalizing={finalizing}
              outcome={outcome}
              errorMessage={finalizeError}
              onStart={() => finalizeMutation.mutate()}
              onRetry={() => finalizeMutation.mutate()}
              onBack={() => setStep(4)}
            />
          ) : null}
        </motion.div>
      </AnimatePresence>

      {stepMutation.isError ? (
        <p className="mt-4 text-sm text-[color:var(--color-novarad-red)]">
          Couldn&apos;t save that step. Adjust and try again.
        </p>
      ) : null}

      <Dialog
        open={confirmCancel}
        onClose={() => setConfirmCancel(false)}
        title="Cancel this validation?"
        description="You can pick the study back up from the worklist. Nothing has been written to PACS or RIS yet."
      >
        <DialogActions>
          <Button variant="ghost" onClick={() => setConfirmCancel(false)}>
            Keep working
          </Button>
          <Button
            variant="danger"
            onClick={() => {
              setConfirmCancel(false);
              cancelMutation.mutate();
            }}
            loading={cancelMutation.isPending}
          >
            Cancel validation
          </Button>
        </DialogActions>
      </Dialog>
    </div>
  );
}
