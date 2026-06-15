"use client";

import { useMutation } from "@tanstack/react-query";
import { AxiosError } from "axios";
import { CheckCircle2, FileArchive, Upload, XCircle } from "lucide-react";
import { useRef, useState } from "react";

import { Button } from "@/components/ui/button";
import { Spinner } from "@/components/ui/spinner";
import { billingApi } from "@/lib/api";
import { type RvuImport, type RvuQuarter } from "@/lib/types";
import { cn } from "@/lib/utils";

const QUARTER_ORDINAL: Record<RvuQuarter, string> = { A: "1st", B: "2nd", C: "3rd", D: "4th" };
const QUARTER_LABEL: Record<RvuQuarter, string> = {
  A: "1st quarter (January to March)",
  B: "2nd quarter (April to June)",
  C: "3rd quarter (July to September)",
  D: "4th quarter (October to December)",
};

interface RvuImportUploaderProps {
  year: number;
  quarter: RvuQuarter;
  onImported: (result: RvuImport) => void;
}

// 25 MB — matches the API's MaxRvuUploadBytes cap so we reject early client-side.
const MAX_BYTES = 25 * 1024 * 1024;

export function RvuImportUploader({
  year,
  quarter,
  onImported,
}: RvuImportUploaderProps) {
  const [open, setOpen] = useState(false);
  const [dragging, setDragging] = useState(false);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [lastResult, setLastResult] = useState<RvuImport | null>(null);
  const fileInputRef = useRef<HTMLInputElement | null>(null);

  const upload = useMutation({
    mutationFn: (file: File) => billingApi.importRvu(file, year, quarter),
    onSuccess: (result) => {
      setLastResult(result);
      setErrorMessage(null);
      onImported(result);
    },
    onError: (err) => {
      const ax = err as AxiosError<{ error?: string }>;
      setErrorMessage(
        ax.response?.data?.error ??
          (ax.response?.status === 403
            ? "Importing is NRS/Admin only."
            : "Couldn't process that file."),
      );
    },
  });

  function handleFile(file: File | null | undefined) {
    if (!file) return;
    if (!/\.(zip|csv|txt)$/i.test(file.name)) {
      setErrorMessage("File must be a CMS PPRRVU .zip (or the bare .csv/.txt).");
      return;
    }
    if (file.size > MAX_BYTES) {
      setErrorMessage("File exceeds the 25 MB upload cap.");
      return;
    }
    setErrorMessage(null);
    upload.mutate(file);
  }

  if (!open) {
    return (
      <div className="mb-4 flex justify-end">
        <Button variant="secondary" size="sm" onClick={() => setOpen(true)}>
          <Upload className="size-4" />
          Import PPRRVU file
        </Button>
      </div>
    );
  }

  return (
    <div className="mb-4 rounded-lg border border-[color:var(--color-border)] bg-[color:var(--color-surface)] p-4">
      <div className="flex items-start justify-between gap-3 mb-3">
        <div>
          <h2
            className="text-base font-medium"
            style={{ fontFamily: "var(--font-display)" }}
          >
            Import {year} {QUARTER_ORDINAL[quarter]}-quarter RVU values
          </h2>
          <p className="text-xs text-[color:var(--color-muted-fg)] mt-0.5">
            Drop the CMS PPRRVU release (
            <code className="font-mono">RVU{String(year).slice(2)}
            {quarter}.zip</code>) or the bare <code className="font-mono">.csv</code>.
            Loads into the <strong>{year}</strong>{" "}
            <strong>{QUARTER_LABEL[quarter]}</strong> snapshot.
          </p>
        </div>
        <Button variant="ghost" size="sm" onClick={() => setOpen(false)}>
          Close
        </Button>
      </div>

      <div
        onDragOver={(e) => {
          e.preventDefault();
          setDragging(true);
        }}
        onDragLeave={() => setDragging(false)}
        onDrop={(e) => {
          e.preventDefault();
          setDragging(false);
          handleFile(e.dataTransfer.files?.[0]);
        }}
        onClick={() => fileInputRef.current?.click()}
        role="button"
        tabIndex={0}
        onKeyDown={(e) => {
          if (e.key === "Enter" || e.key === " ") {
            e.preventDefault();
            fileInputRef.current?.click();
          }
        }}
        className={cn(
          "flex flex-col items-center justify-center gap-2 rounded-md border-2 border-dashed px-6 py-10 cursor-pointer transition-colors",
          "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-accent)]/50",
          dragging
            ? "border-[color:var(--color-accent)] bg-[color:var(--color-accent)]/8"
            : "border-[color:var(--color-border)] hover:bg-[color:var(--color-surface-2)]/50",
          upload.isPending && "pointer-events-none opacity-70",
        )}
      >
        {upload.isPending ? (
          <>
            <Spinner size={20} />
            <span className="text-sm text-[color:var(--color-muted-fg)]">
              Parsing &amp; importing…
            </span>
          </>
        ) : (
          <>
            <FileArchive className="size-7 text-[color:var(--color-accent)]" />
            <span className="text-sm font-medium">
              Drop your PPRRVU .zip here, or click to choose
            </span>
            <span className="text-xs text-[color:var(--color-muted-fg)]">
              Max 25 MB · we read the <code className="font-mono">PPRRVU</code> table
            </span>
          </>
        )}
        <input
          ref={fileInputRef}
          type="file"
          accept=".zip,.csv,.txt,application/zip"
          hidden
          onChange={(e) => handleFile(e.target.files?.[0])}
        />
      </div>

      {errorMessage ? (
        <div className="mt-3 flex items-start gap-2 rounded-md border border-[color:var(--color-novarad-red)]/40 bg-[color:var(--color-novarad-red)]/10 px-3 py-2 text-sm text-[color:var(--color-novarad-red)]">
          <XCircle className="size-4 mt-0.5" />
          {errorMessage}
        </div>
      ) : null}

      {lastResult ? <RvuResultSummary result={lastResult} /> : null}
    </div>
  );
}

function RvuResultSummary({ result }: { result: RvuImport }) {
  return (
    <div className="mt-3 rounded-md border border-[oklch(0.72_0.14_160)]/40 bg-[oklch(0.72_0.14_160)]/10 px-3 py-3">
      <div className="flex items-center gap-2 mb-1">
        <CheckCircle2 className="size-4 text-[oklch(0.72_0.14_160)]" />
        <span className="text-sm font-medium">
          Imported {result.fileName} ({result.parsedRows} rows)
        </span>
      </div>
      <div className="text-xs text-[color:var(--color-muted-fg)] ml-6 flex flex-wrap gap-x-4 gap-y-1">
        <span>
          <strong className="text-[color:var(--color-accent)]">
            {result.insertedRows}
          </strong>{" "}
          inserted
        </span>
        <span>
          <strong>{result.updatedRows}</strong> updated
        </span>
        {result.skippedRows > 0 ? (
          <span>
            <strong className="text-[color:var(--color-caution)]">
              {result.skippedRows}
            </strong>{" "}
            skipped
          </span>
        ) : null}
      </div>
      {result.errors.length > 0 ? (
        <details className="mt-3">
          <summary className="cursor-pointer text-xs text-[color:var(--color-caution)] hover:underline">
            View {result.errors.length} per-row issue
            {result.errors.length === 1 ? "" : "s"}
          </summary>
          <ul className="mt-2 max-h-48 overflow-y-auto rounded-md border border-[color:var(--color-border)] bg-[color:var(--color-surface-2)]/40 px-3 py-2 text-xs space-y-1">
            {result.errors.map((e, i) => (
              <li key={i} className="font-mono">
                <span className="text-[color:var(--color-muted-fg)]">
                  row {e.row}
                </span>
                {e.cpt ? <span> · {e.cpt}</span> : null} — {e.message}
              </li>
            ))}
          </ul>
        </details>
      ) : null}
    </div>
  );
}
