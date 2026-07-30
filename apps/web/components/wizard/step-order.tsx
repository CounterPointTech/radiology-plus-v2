"use client";

import { useQuery } from "@tanstack/react-query";
import { AlertCircle, FilePlus2 } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardBody, CardHeader, CardSubtitle, CardTitle } from "@/components/ui/card";
import { Spinner } from "@/components/ui/spinner";
import { techApi } from "@/lib/api";
import type { CandidateOrder } from "@/lib/types";
import { cn, formatDateTime } from "@/lib/utils";

export function StepOrder({
  patientPid,
  selectedOrderId,
  createOrderRequested,
  onSelect,
  onToggleCreate,
  onBack,
  onContinue,
  saving,
}: {
  patientPid: string | null;
  selectedOrderId: number | null;
  createOrderRequested: boolean;
  onSelect: (orderId: number | null) => void;
  onToggleCreate: (next: boolean) => void;
  onBack: () => void;
  onContinue: () => void;
  saving: boolean;
}) {
  const ordersQuery = useQuery({
    queryKey: ["orders", patientPid],
    queryFn: () => techApi.ordersByPatient(patientPid!),
    enabled: !!patientPid,
  });

  const orders = ordersQuery.data ?? [];

  return (
    <Card>
      <CardHeader>
        <CardTitle>Pick the matching order</CardTitle>
        <CardSubtitle>
          Match the study to a scheduled RIS order. If nothing fits, ask for an
          order to be created.
        </CardSubtitle>
      </CardHeader>
      <CardBody className="space-y-5">
        {!patientPid ? (
          <p className="text-sm text-[color:var(--color-muted-fg)]">
            No PID on this study, so we can&apos;t pull orders. Continue without an
            order, or flip on the create-order toggle below.
          </p>
        ) : ordersQuery.isLoading ? (
          <div className="flex items-center gap-3 text-sm text-[color:var(--color-muted-fg)]">
            <Spinner size={16} />
            Loading orders for {patientPid}…
          </div>
        ) : ordersQuery.isError ? (
          <div className="rounded-md border border-[color:var(--color-novarad-red)]/40 bg-[color:var(--color-novarad-red)]/10 px-3 py-2 text-sm text-[color:var(--color-novarad-red)] flex items-center gap-2">
            <AlertCircle className="size-4" />
            Couldn&apos;t load orders for {patientPid}.
          </div>
        ) : orders.length === 0 ? (
          <p className="text-sm text-[color:var(--color-muted-fg)]">
            No orders found for {patientPid}. Toggle &ldquo;Create order&rdquo; below to flag this.
          </p>
        ) : (
          <ul className="space-y-2 max-h-[28rem] overflow-y-auto pr-1">
            {orders.map((o) => (
              <OrderRow
                key={o.orderId}
                order={o}
                selected={selectedOrderId === o.orderId}
                disabled={createOrderRequested}
                onClick={() =>
                  onSelect(selectedOrderId === o.orderId ? null : o.orderId)
                }
              />
            ))}
          </ul>
        )}

        <label
          className={cn(
            "flex items-start gap-3 rounded-md border border-[color:var(--color-border)] p-3 cursor-pointer",
            createOrderRequested && "border-[color:var(--color-accent)]/60 bg-[color:var(--color-accent)]/8",
          )}
        >
          {/* Accent, not caution: requesting an order is a normal choice, not an error. */}
          <input
            type="checkbox"
            checked={createOrderRequested}
            onChange={(e) => {
              const v = e.target.checked;
              if (v) onSelect(null);
              onToggleCreate(v);
            }}
            className="mt-1 size-4 accent-[color:var(--color-accent)]"
          />
          <div className="space-y-1">
            <div className="flex items-center gap-2 text-sm font-medium">
              <FilePlus2 className="size-4 text-[color:var(--color-accent)]" />
              Can&apos;t find the order — request one is created
            </div>
            <p className="text-xs text-[color:var(--color-muted-fg)]">
              Flags this validation as needing a follow-up. The Do-the-Do will run
              the patient-side steps and skip the RIS-side writes.
            </p>
          </div>
        </label>
      </CardBody>
      <div className="px-5 py-3 border-t border-[color:var(--color-border)] flex items-center justify-between">
        <Button variant="ghost" onClick={onBack}>
          Back
        </Button>
        <Button
          onClick={onContinue}
          loading={saving}
          disabled={!selectedOrderId && !createOrderRequested}
        >
          Continue to reason
        </Button>
      </div>
    </Card>
  );
}

function OrderRow({
  order,
  selected,
  disabled,
  onClick,
}: {
  order: CandidateOrder;
  selected: boolean;
  disabled: boolean;
  onClick: () => void;
}) {
  const status = order.status?.toLowerCase() ?? "";
  let variant: "neutral" | "accent" | "caution" | "danger" = "neutral";
  if (status.includes("cancel")) variant = "danger";
  else if (status.includes("schedul")) variant = "accent";
  else if (status.includes("complete")) variant = "neutral";
  else if (status) variant = "caution";

  return (
    <li>
      <button
        type="button"
        disabled={disabled}
        onClick={onClick}
        className={cn(
          "w-full text-left rounded-md border px-3 py-2.5 transition-colors",
          selected
            ? "border-[color:var(--color-accent)] bg-[color:var(--color-accent)]/10"
            : "border-[color:var(--color-border)] hover:bg-[color:var(--color-surface-2)]",
          disabled && "opacity-50 cursor-not-allowed pointer-events-none",
        )}
      >
        <div className="flex items-center justify-between gap-3">
          <div className="font-mono text-sm">{order.accessionNumber}</div>
          <Badge variant={variant}>{order.status || "—"}</Badge>
        </div>
        <div className="mt-1 text-sm">
          {order.description ?? <span className="text-[color:var(--color-muted-fg)]">no description</span>}
        </div>
        <div className="mt-1 text-xs text-[color:var(--color-muted-fg)] flex flex-wrap gap-x-3">
          <span>Order #{order.orderId}</span>
          {order.creationDate ? <span>Created {formatDateTime(order.creationDate)}</span> : null}
          {order.referringPhysicianId ? (
            <span>Ref phys #{order.referringPhysicianId}</span>
          ) : null}
        </div>
      </button>
    </li>
  );
}
