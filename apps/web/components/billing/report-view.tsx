"use client";

import DOMPurify from "dompurify";
import { Printer } from "lucide-react";
import { useCallback, useEffect, useMemo } from "react";

import { Button } from "@/components/ui/button";
import type { ReportContent } from "@/lib/types";
import { formatDate, formatDateTime } from "@/lib/utils";

export function ReportView({ content }: { content: ReportContent }) {
  const sanitized = useMemo(() => {
    if (!content.reportText) return "";
    if ((content.reportFormat ?? "").toUpperCase() === "HTML") {
      return DOMPurify.sanitize(content.reportText, { USE_PROFILES: { html: true } });
    }
    return "";
  }, [content.reportText, content.reportFormat]);

  const isHtml = (content.reportFormat ?? "").toUpperCase() === "HTML";

  // window.print() doesn't expose its own end signal, so use the standard
  // afterprint event to remove the class. Cleanup handles unmount-mid-print.
  const handleAfterPrint = useCallback(() => {
    document.documentElement.classList.remove("printing-report");
  }, []);

  useEffect(() => {
    window.addEventListener("afterprint", handleAfterPrint);
    return () => {
      window.removeEventListener("afterprint", handleAfterPrint);
      document.documentElement.classList.remove("printing-report");
    };
  }, [handleAfterPrint]);

  function print() {
    document.documentElement.classList.add("printing-report");
    // Defer to next frame so the class lands before the print dialog snapshots layout.
    requestAnimationFrame(() => window.print());
  }

  const patientName = [content.patientLastName, content.patientFirstName]
    .filter(Boolean)
    .join(", ") || "—";

  return (
    <div className="report-print-target rounded-md border border-[color:var(--color-border)] bg-[color:var(--color-surface)] p-4 mt-2">
      <div className="report-print-no-print flex items-center justify-end mb-2">
        <Button
          variant="secondary"
          size="sm"
          onClick={print}
          title="Print just the report"
        >
          <Printer className="size-4" /> Print
        </Button>
      </div>

      <header className="grid grid-cols-2 sm:grid-cols-3 gap-x-6 gap-y-1.5 text-xs mb-3 pb-3 border-b border-[color:var(--color-border)]">
        <Field label="Patient" value={patientName} />
        <Field label="MRN" value={content.patientPid ?? "—"} mono />
        <Field
          label="DOB"
          value={content.patientBirthDate ? formatDate(content.patientBirthDate) : "—"}
        />
        <Field label="Sex" value={content.patientGender ?? "—"} />
        <Field label="Accession" value={content.accession ?? "—"} mono />
        <Field
          label="Modality"
          value={`${content.modality ?? "—"}${content.studyDate ? " · " + formatDateTime(content.studyDate) : ""}`}
        />
        <Field
          label="Signed by"
          value={content.signingPhysicianName ?? (
            content.signingPhysicianId != null
              ? `Physician #${content.signingPhysicianId}`
              : "—"
          )}
        />
        <Field
          label="Signed at"
          value={content.signedAt ? formatDateTime(content.signedAt) : "—"}
        />
        <Field
          label="Format"
          value={(content.reportFormat ?? "—")}
        />
      </header>

      {content.reportText == null ? (
        <p className="text-xs italic text-[color:var(--color-muted-fg)]">
          This report has no text body.
        </p>
      ) : isHtml ? (
        <div
          className="report-body text-sm"
          // dompurify sanitizes above before this assignment.
          // eslint-disable-next-line react/no-danger
          dangerouslySetInnerHTML={{ __html: sanitized }}
        />
      ) : (
        <pre className="report-body whitespace-pre-wrap text-sm font-mono text-[color:var(--color-base-fg)]">
          {content.reportText}
        </pre>
      )}
    </div>
  );
}

function Field({
  label,
  value,
  mono = false,
}: {
  label: string;
  value: string;
  mono?: boolean;
}) {
  return (
    <div>
      <div className="text-[10px] uppercase tracking-[0.15em] text-[color:var(--color-muted-fg)]">
        {label}
      </div>
      <div className={mono ? "font-mono" : ""}>{value}</div>
    </div>
  );
}
