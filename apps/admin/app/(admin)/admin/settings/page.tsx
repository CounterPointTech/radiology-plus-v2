"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AxiosError } from "axios";
import { Database, PlugZap, Save, Trash2 } from "lucide-react";
import { useEffect, useState } from "react";

import { Field, inputCls, textareaCls } from "@/components/notifications/notification-bits";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Spinner } from "@/components/ui/spinner";
import { settingsApi } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { formatDateTime } from "@/lib/utils";

export default function SettingsPage() {
  const qc = useQueryClient();
  const { user: me, isHydrated } = useAuth();

  const [host, setHost] = useState("");
  const [port, setPort] = useState(5432);
  const [database, setDatabase] = useState("");
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [useSsl, setUseSsl] = useState(true);
  const [auditTable, setAuditTable] = useState("");
  const [notes, setNotes] = useState("");
  const [confirmRemove, setConfirmRemove] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const tenant = useQuery({
    queryKey: ["settings-tenant"],
    queryFn: () => settingsApi.tenant(),
    enabled: !!me,
  });
  const connection = useQuery({
    queryKey: ["settings-novarad"],
    queryFn: () => settingsApi.novarad(),
    enabled: !!me,
  });
  const configured = connection.data?.configured ?? false;

  // Seed the form from the saved settings (password never comes back).
  useEffect(() => {
    const s = connection.data?.settings;
    if (s) {
      setHost(s.host);
      setPort(s.port);
      setDatabase(s.database);
      setUsername(s.username);
      setUseSsl(s.useSsl);
      setAuditTable(s.novaradAuditTable);
      setNotes(s.notes ?? "");
    }
  }, [connection.data]);

  const saveMut = useMutation({
    mutationFn: () =>
      settingsApi.saveNovarad({
        host: host.trim(),
        port,
        database: database.trim(),
        username: username.trim(),
        password: password.trim() ? password.trim() : null,
        useSsl,
        novaradAuditTable: auditTable.trim() ? auditTable.trim() : null,
        notes: notes.trim() ? notes.trim() : null,
      }),
    onSuccess: () => {
      setPassword("");
      setError(null);
      setNotice("Connection saved — new Novarad reads use it immediately.");
      void qc.invalidateQueries({ queryKey: ["settings-novarad"] });
    },
    onError: (err) => {
      const ax = err as AxiosError<{ error?: string }>;
      setNotice(null);
      setError(ax.response?.data?.error ?? "Couldn't save the connection. Try again.");
    },
  });

  const testMut = useMutation({ mutationFn: () => settingsApi.testNovarad() });

  const removeMut = useMutation({
    mutationFn: () => settingsApi.deleteNovarad(),
    onSuccess: () => {
      setConfirmRemove(false);
      setNotice("Connection removed — federated sign-in and Novarad reads are disabled until a new one is saved.");
      void qc.invalidateQueries({ queryKey: ["settings-novarad"] });
    },
    onError: () => setError("Couldn't remove the connection. Try again."),
  });

  if (!isHydrated || !me || connection.isLoading) {
    return (
      <div className="min-h-[60vh] flex items-center justify-center">
        <Spinner size={28} />
      </div>
    );
  }

  return (
    <div className="mx-auto max-w-4xl px-6 py-8">
      <div className="mb-6 rise-in">
        <p className="text-[10px] uppercase tracking-[0.3em] text-[color:var(--color-accent)]">
          Admin
        </p>
        <h1 className="text-3xl mt-1" style={{ fontFamily: "var(--font-display)" }}>
          Settings<span className="caret-blink text-[color:var(--color-accent)]">▍</span>
        </h1>
        {tenant.data ? (
          <p className="text-sm text-[color:var(--color-muted-fg)] mt-1">
            Tenant <span className="font-mono">{tenant.data.code}</span> —{" "}
            {tenant.data.displayName}
          </p>
        ) : null}
      </div>

      <div className="space-y-6">
        <div className="flex flex-wrap items-center gap-3">
          <h2
            className="inline-flex items-center gap-2 text-lg"
            style={{ fontFamily: "var(--font-display)" }}
          >
            <Database className="size-5 text-[color:var(--color-accent)]" />
            Novarad connection
          </h2>
          {configured ? (
            <Badge variant="success">configured</Badge>
          ) : (
            <Badge variant="caution">not configured</Badge>
          )}
          {connection.data?.settings ? (
            <span className="text-xs text-[color:var(--color-muted-fg)]">
              updated {formatDateTime(connection.data.settings.updatedAt)}
            </span>
          ) : null}
        </div>
        <p className="text-sm text-[color:var(--color-muted-fg)]">
          This is the tenant&apos;s PACS/RIS database over the site-to-site VPN. It powers
          Novarad sign-in, billing reads, scripting, and the facility import. The password is
          encrypted at rest and never shown again after saving.
        </p>

        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Host" required>
            <input value={host} onChange={(e) => setHost(e.target.value)} className={`${inputCls} font-mono`} />
          </Field>
          <Field label="Port" required>
            <input
              type="number"
              min={1}
              max={65535}
              value={port}
              onChange={(e) => setPort(Number(e.target.value) || 5432)}
              className={inputCls}
            />
          </Field>
          <Field label="Database" required>
            <input value={database} onChange={(e) => setDatabase(e.target.value)} className={`${inputCls} font-mono`} />
          </Field>
          <Field label="Username" required>
            <input value={username} onChange={(e) => setUsername(e.target.value)} className={`${inputCls} font-mono`} />
          </Field>
          <Field
            label="Password"
            required={!configured}
            hint={configured ? "Leave blank to keep the saved password." : null}
          >
            <input
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              placeholder={configured ? "•••••••• (unchanged)" : ""}
              autoComplete="new-password"
              className={inputCls}
            />
          </Field>
          <Field label="Novarad audit table" hint="Where dual-audit writes land on the Novarad side.">
            <input
              value={auditTable}
              onChange={(e) => setAuditTable(e.target.value)}
              placeholder="object_store.audit"
              className={`${inputCls} font-mono`}
            />
          </Field>
        </div>
        <label className="inline-flex items-center gap-2 text-sm cursor-pointer select-none">
          <input
            type="checkbox"
            checked={useSsl}
            onChange={(e) => setUseSsl(e.target.checked)}
            className="size-4 accent-[color:var(--color-accent)]"
          />
          Require SSL
        </label>
        <Field label="Notes">
          <textarea
            value={notes}
            onChange={(e) => setNotes(e.target.value)}
            rows={2}
            className={textareaCls}
          />
        </Field>

        {error ? <p className="text-sm text-[color:var(--color-novarad-red)]">{error}</p> : null}
        {notice ? <p className="text-sm text-[color:var(--color-accent)]">{notice}</p> : null}

        <div className="flex flex-wrap items-center gap-2">
          <Button
            loading={saveMut.isPending}
            onClick={() => {
              setNotice(null);
              if (!host.trim() || !database.trim() || !username.trim()) {
                setError("Host, database, and username are required.");
                return;
              }
              setError(null);
              saveMut.mutate();
            }}
          >
            <Save className="size-4" />
            Save connection
          </Button>
          <Button variant="secondary" loading={testMut.isPending} onClick={() => testMut.mutate()}>
            <PlugZap className="size-4" />
            Test connection
          </Button>
          {configured ? (
            confirmRemove ? (
              <>
                <span className="text-xs text-[color:var(--color-caution)]">
                  Remove it? Novarad sign-in and reads stop working.
                </span>
                <Button
                  variant="danger"
                  size="sm"
                  loading={removeMut.isPending}
                  onClick={() => removeMut.mutate()}
                >
                  Confirm
                </Button>
                <Button variant="ghost" size="sm" onClick={() => setConfirmRemove(false)}>
                  Cancel
                </Button>
              </>
            ) : (
              <Button variant="ghost" onClick={() => setConfirmRemove(true)}>
                <Trash2 className="size-4" />
                Remove
              </Button>
            )
          ) : null}
        </div>

        {testMut.data ? (
          <p
            className={`text-sm ${
              testMut.data.ok
                ? "text-[color:var(--color-success)]"
                : "text-[color:var(--color-novarad-red)]"
            }`}
          >
            {testMut.data.ok
              ? `Connected in ${testMut.data.durationMs}ms — ${testMut.data.serverVersion?.split(",")[0] ?? "OK"}`
              : `Failed: ${testMut.data.error}`}
          </p>
        ) : null}
      </div>
    </div>
  );
}
