"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AxiosError } from "axios";
import { KeyRound, MailCheck, Save, Trash2 } from "lucide-react";
import { useEffect, useState } from "react";

import { Field, inputCls } from "@/components/notifications/notification-bits";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Spinner } from "@/components/ui/spinner";
import { notificationsApi } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { formatDateTime } from "@/lib/utils";

export default function EmailSettingsPage() {
  const qc = useQueryClient();
  const { user, isHydrated } = useAuth();

  const [graphTenantId, setGraphTenantId] = useState("");
  const [clientId, setClientId] = useState("");
  const [clientSecret, setClientSecret] = useState("");
  const [fromAddress, setFromAddress] = useState("");
  const [testRecipient, setTestRecipient] = useState("");
  const [confirmRemove, setConfirmRemove] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const settings = useQuery({
    queryKey: ["notif-graph-settings"],
    queryFn: () => notificationsApi.graphSettings(),
    enabled: !!user,
  });
  const configured = settings.data?.configured ?? false;

  // Seed the form from the saved settings (secret never comes back).
  useEffect(() => {
    const s = settings.data?.settings;
    if (s) {
      setGraphTenantId(s.graphTenantId);
      setClientId(s.clientId);
      setFromAddress(s.fromAddress);
    }
  }, [settings.data]);

  const saveMut = useMutation({
    mutationFn: () =>
      notificationsApi.saveGraphSettings({
        graphTenantId: graphTenantId.trim(),
        clientId: clientId.trim(),
        clientSecret: clientSecret.trim() ? clientSecret.trim() : null,
        fromAddress: fromAddress.trim(),
      }),
    onSuccess: () => {
      setClientSecret("");
      setError(null);
      setNotice("Settings saved.");
      void qc.invalidateQueries({ queryKey: ["notif-graph-settings"] });
    },
    onError: (err) => {
      const ax = err as AxiosError<{ error?: string }>;
      setNotice(null);
      setError(ax.response?.data?.error ?? "Couldn't save the settings. Try again.");
    },
  });

  const testMut = useMutation({
    mutationFn: () =>
      notificationsApi.testGraphSettings(testRecipient.trim() ? testRecipient.trim() : null),
    onSuccess: () => setError(null),
  });

  const removeMut = useMutation({
    mutationFn: () => notificationsApi.deleteGraphSettings(),
    onSuccess: () => {
      setConfirmRemove(false);
      setGraphTenantId("");
      setClientId("");
      setClientSecret("");
      setFromAddress("");
      setNotice("Settings removed — email falls back to the server-configured account, if any.");
      void qc.invalidateQueries({ queryKey: ["notif-graph-settings"] });
    },
    onError: () => setError("Couldn't remove the settings. Try again."),
  });

  if (!isHydrated || !user || settings.isLoading) {
    return (
      <div className="min-h-[60vh] flex items-center justify-center">
        <Spinner size={28} />
      </div>
    );
  }

  return (
    <div className="max-w-2xl space-y-6">
      <div className="flex flex-wrap items-center gap-3">
        <h2 className="text-lg" style={{ fontFamily: "var(--font-display)" }}>
          Microsoft 365 email
        </h2>
        {configured ? (
          <Badge variant="success">configured</Badge>
        ) : (
          <Badge variant="neutral">not configured</Badge>
        )}
        {settings.data?.settings ? (
          <span className="text-xs text-[color:var(--color-muted-fg)]">
            updated {formatDateTime(settings.data.settings.updatedAt)}
          </span>
        ) : null}
      </div>
      <p className="text-sm text-[color:var(--color-muted-fg)]">
        Email notifications send through a Microsoft Entra app registration using
        client-credentials sign-in. The client secret is encrypted at rest and never
        shown again after saving.
      </p>

      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="Directory (tenant) ID" required>
          <input
            value={graphTenantId}
            onChange={(e) => setGraphTenantId(e.target.value)}
            placeholder="00000000-0000-0000-0000-000000000000"
            className={`${inputCls} font-mono`}
          />
        </Field>
        <Field label="Application (client) ID" required>
          <input
            value={clientId}
            onChange={(e) => setClientId(e.target.value)}
            placeholder="00000000-0000-0000-0000-000000000000"
            className={`${inputCls} font-mono`}
          />
        </Field>
        <Field
          label="Client secret"
          required={!configured}
          hint={
            configured
              ? "Leave blank to keep the saved secret."
              : "From the app registration's Certificates & secrets page."
          }
        >
          <input
            type="password"
            value={clientSecret}
            onChange={(e) => setClientSecret(e.target.value)}
            placeholder={configured ? "•••••••• (unchanged)" : ""}
            autoComplete="new-password"
            className={inputCls}
          />
        </Field>
        <Field label="Send as (mailbox)" required>
          <input
            value={fromAddress}
            onChange={(e) => setFromAddress(e.target.value)}
            placeholder="alerts@yourdomain.com"
            className={inputCls}
          />
        </Field>
      </div>

      {error ? <p className="text-sm text-[color:var(--color-novarad-red)]">{error}</p> : null}
      {notice ? <p className="text-sm text-[color:var(--color-accent)]">{notice}</p> : null}

      <div className="flex flex-wrap items-center gap-2">
        <Button
          loading={saveMut.isPending}
          onClick={() => {
            setNotice(null);
            if (!graphTenantId.trim() || !clientId.trim() || !fromAddress.trim()) {
              setError("Tenant ID, client ID and the send-as mailbox are all required.");
              return;
            }
            setError(null);
            saveMut.mutate();
          }}
        >
          <Save className="size-4" />
          Save settings
        </Button>
        {configured ? (
          confirmRemove ? (
            <>
              <span className="text-xs text-[color:var(--color-caution)]">
                Remove the saved credentials?
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

      <div className="rounded-lg border border-[color:var(--color-border)] bg-[color:var(--color-surface)] px-4 py-4 space-y-3">
        <h3 className="inline-flex items-center gap-2 text-sm font-medium">
          <KeyRound className="size-4 text-[color:var(--color-accent)]" />
          Test the connection
        </h3>
        <p className="text-xs text-[color:var(--color-muted-fg)]">
          Tests the <em>saved</em> settings. Leave the recipient blank to only check the
          sign-in, or enter an address to send a real test message.
        </p>
        <div className="flex flex-wrap items-center gap-2">
          <input
            value={testRecipient}
            onChange={(e) => setTestRecipient(e.target.value)}
            placeholder="Optional test recipient"
            aria-label="Test recipient"
            className={`${inputCls} w-64`}
          />
          <Button
            variant="secondary"
            loading={testMut.isPending}
            onClick={() => testMut.mutate()}
          >
            <MailCheck className="size-4" />
            Test
          </Button>
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
              ? testMut.data.sentTestEmail
                ? "Success — test message sent."
                : "Success — sign-in works."
              : `Failed: ${testMut.data.error}`}
          </p>
        ) : null}
      </div>
    </div>
  );
}
