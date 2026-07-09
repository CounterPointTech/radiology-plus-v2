"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AlertCircle, CheckCircle2, Plug } from "lucide-react";
import { useState } from "react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Spinner } from "@/components/ui/spinner";
import { billingApi } from "@/lib/api";
import type { RvuConnectionTest } from "@/lib/types";
import { formatDateTime } from "@/lib/utils";

/**
 * The M*Modal connection settings card: a setup form when no connection is
 * configured, and a connected summary with Test / Edit / Remove once one is.
 * Backed by the per-tenant encrypted row in tenancy.mmodal_connections — the
 * same one the `set-mmodal-connection` CLI writes. NRS/Admin only (enforced
 * server-side).
 */
export function MModalConnectionCard() {
  const qc = useQueryClient();
  const conn = useQuery({
    queryKey: ["mmodal-connection"],
    queryFn: () => billingApi.getMModalConnection(),
  });
  const info = conn.data?.connection ?? null;
  const configured = conn.data?.configured ?? false;

  const [editing, setEditing] = useState(false);
  const [host, setHost] = useState("");
  const [port, setPort] = useState("1433");
  const [database, setDatabase] = useState("ClinicalDataStore");
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [issuerKey, setIssuerKey] = useState("");
  const [test, setTest] = useState<RvuConnectionTest | null>(null);
  const [confirmRemove, setConfirmRemove] = useState(false);

  const invalidate = () => {
    qc.invalidateQueries({ queryKey: ["mmodal-connection"] });
    qc.invalidateQueries({ queryKey: ["mmodal-sync-status"] });
    qc.invalidateQueries({ queryKey: ["mmodal-issuers"] });
  };

  const saveMut = useMutation({
    mutationFn: () =>
      billingApi.saveMModalConnection({
        host: host.trim(),
        port: Number(port) || 1433,
        database: database.trim() || "ClinicalDataStore",
        username: username.trim(),
        password: password.length ? password : undefined,
        useSsl: true,
        trustServerCert: true,
        issuerKey: issuerKey.trim() ? issuerKey.trim() : null,
      }),
    onSuccess: () => {
      setEditing(false);
      setPassword("");
      setTest(null);
      invalidate();
    },
  });
  const testMut = useMutation({
    mutationFn: () => billingApi.testMModalConnection(),
    onSuccess: (r) => setTest(r),
  });
  const deleteMut = useMutation({
    mutationFn: () => billingApi.deleteMModalConnection(),
    onSuccess: () => {
      setConfirmRemove(false);
      setEditing(false);
      setTest(null);
      invalidate();
    },
  });

  function startEdit() {
    if (info) {
      setHost(info.host);
      setPort(String(info.port));
      setDatabase(info.database);
      setUsername(info.username);
      setIssuerKey(info.issuerKey ?? "");
    }
    setPassword("");
    setTest(null);
    setEditing(true);
  }

  if (conn.isLoading) {
    return (
      <div className="rounded-lg border border-[color:var(--color-border)] bg-[color:var(--color-surface)] p-4">
        <Spinner size={18} />
      </div>
    );
  }

  const showForm = !configured || editing;
  const canSave =
    host.trim().length > 0 &&
    username.trim().length > 0 &&
    (configured || password.length > 0); // password required on first setup

  return (
    <div className="rounded-lg border border-[color:var(--color-border)] bg-[color:var(--color-surface)] p-4 space-y-3">
      <div className="flex items-center gap-2">
        <Plug className="size-4 text-[color:var(--color-accent)]" />
        <h3 className="text-sm font-medium">M*Modal connection</h3>
        {configured && !editing ? <Badge variant="success">connected</Badge> : null}
      </div>

      {showForm ? (
        <div className="space-y-3">
          {!configured ? (
            <p className="text-xs text-[color:var(--color-muted-fg)]">
              Point this tenant at its M*Modal ClinicalDataStore (SQL Server). The password is
              encrypted at rest; nothing is written until you configure it here.
            </p>
          ) : null}
          <div className="grid gap-3 sm:grid-cols-2 max-w-2xl">
            <Field label="Host" value={host} onChange={setHost} placeholder="127.0.0.1" />
            <Field label="Port" value={port} onChange={setPort} placeholder="1433" />
            <Field label="Database" value={database} onChange={setDatabase} placeholder="ClinicalDataStore" />
            <Field label="Username" value={username} onChange={setUsername} placeholder="sa" />
            <Field
              label={configured ? "Password (leave blank to keep)" : "Password"}
              value={password}
              onChange={setPassword}
              placeholder={configured ? "••••••••" : "SQL login password"}
              type="password"
            />
            <Field
              label="Default facility issuer (optional GUID)"
              value={issuerKey}
              onChange={setIssuerKey}
              placeholder="preselected in the picker"
            />
          </div>
          <div className="flex items-center gap-2">
            <Button
              variant="primary"
              size="sm"
              loading={saveMut.isPending}
              disabled={!canSave}
              onClick={() => saveMut.mutate()}
            >
              Save connection
            </Button>
            {configured ? (
              <Button
                variant="ghost"
                size="sm"
                onClick={() => {
                  setEditing(false);
                  setTest(null);
                }}
              >
                Cancel
              </Button>
            ) : null}
            {saveMut.isError ? (
              <span className="text-sm text-[color:var(--color-novarad-red)]">
                Couldn&apos;t save — check the fields.
              </span>
            ) : null}
          </div>
        </div>
      ) : info ? (
        <div className="space-y-2">
          <div className="text-sm font-mono">
            {info.username}@{info.host}:{info.port}/{info.database}
          </div>
          <div className="text-xs text-[color:var(--color-muted-fg)]">
            Updated {formatDateTime(info.updatedAt)}
            {info.issuerKey ? " · default facility set" : " · no default facility"}
          </div>
          {test ? (
            test.ok ? (
              <p className="text-sm flex items-center gap-2">
                <CheckCircle2 className="size-4 text-[oklch(0.72_0.14_160)]" />
                Connected — {test.issuerCount} facilit{test.issuerCount === 1 ? "y" : "ies"} reachable.
              </p>
            ) : (
              <p className="text-sm flex items-center gap-2 text-[color:var(--color-novarad-red)]">
                <AlertCircle className="size-4" />
                Couldn&apos;t connect: {test.error}
              </p>
            )
          ) : null}
          <div className="flex flex-wrap items-center gap-2">
            <Button variant="secondary" size="sm" loading={testMut.isPending} onClick={() => testMut.mutate()}>
              Test connection
            </Button>
            <Button variant="ghost" size="sm" onClick={startEdit}>
              Edit
            </Button>
            {confirmRemove ? (
              <>
                <span className="text-xs text-[color:var(--color-caution)]">Remove the connection?</span>
                <Button variant="danger" size="sm" loading={deleteMut.isPending} onClick={() => deleteMut.mutate()}>
                  Confirm remove
                </Button>
                <Button variant="ghost" size="sm" onClick={() => setConfirmRemove(false)}>
                  Cancel
                </Button>
              </>
            ) : (
              <Button variant="ghost" size="sm" onClick={() => setConfirmRemove(true)}>
                Remove
              </Button>
            )}
          </div>
        </div>
      ) : null}
    </div>
  );
}

function Field({
  label,
  value,
  onChange,
  placeholder,
  type = "text",
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
  placeholder?: string;
  type?: string;
}) {
  return (
    <label className="flex flex-col gap-1 text-sm">
      <span className="text-[10px] uppercase tracking-[0.14em] text-[color:var(--color-muted-fg)]">
        {label}
      </span>
      <input
        type={type}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder={placeholder}
        className="h-9 rounded-md border border-[color:var(--color-border)] bg-[color:var(--color-surface)] px-2 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-accent)]/60"
      />
    </label>
  );
}
