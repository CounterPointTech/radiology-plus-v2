"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AxiosError } from "axios";
import { Dices, KeyRound, LogOut, Pencil, Plus, UserRound, X } from "lucide-react";
import { useState } from "react";

import { Field, inputCls } from "@/components/notifications/notification-bits";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Spinner } from "@/components/ui/spinner";
import { facilitiesApi, usersApi } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import type { AdminUser, FacilityAdmin, Role } from "@/lib/types";
import { isNrs, ROLES } from "@/lib/types";
import { formatDateTime } from "@/lib/utils";

/** Client-side strong password: 20 chars from a copy-paste-safe alphabet. */
function generatePassword(): string {
  const alphabet =
    "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%^&*-_=+";
  const bytes = new Uint32Array(20);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, (b) => alphabet[b % alphabet.length]).join("");
}

interface UserFormState {
  username: string;
  displayName: string;
  email: string;
  role: Role;
  password: string;
  facilityIds: number[];
}

const EMPTY_FORM: UserFormState = {
  username: "",
  displayName: "",
  email: "",
  role: "Tech",
  password: "",
  facilityIds: [],
};

export default function UsersPage() {
  const qc = useQueryClient();
  const { user: me, isHydrated } = useAuth();
  const [editing, setEditing] = useState<"new" | string | null>(null); // "new" | userId
  const [note, setNote] = useState<string | null>(null);

  const users = useQuery({
    queryKey: ["admin-users"],
    queryFn: () => usersApi.list(),
    enabled: !!me,
  });
  const facilities = useQuery({
    queryKey: ["admin-facilities"],
    queryFn: () => facilitiesApi.list(),
    enabled: !!me,
  });
  const invalidate = () => qc.invalidateQueries({ queryKey: ["admin-users"] });

  const toggleMut = useMutation({
    mutationFn: (v: { id: string; isActive: boolean }) => usersApi.setActive(v.id, v.isActive),
    onSuccess: (u) => {
      setNote(
        u.isActive
          ? `${u.username} reactivated.`
          : `${u.username} deactivated — their sign-ins were revoked.`,
      );
      void invalidate();
    },
    onError: (err) => setNote(errText(err, "Couldn't update the user.")),
  });
  const revokeMut = useMutation({
    mutationFn: (id: string) => usersApi.revokeSessions(id),
    onSuccess: (r) => {
      setNote(`${r.revoked} session(s) revoked.`);
      void invalidate();
    },
    onError: (err) => setNote(errText(err, "Couldn't revoke sessions.")),
  });

  if (!isHydrated || !me) {
    return (
      <div className="min-h-[60vh] flex items-center justify-center">
        <Spinner size={28} />
      </div>
    );
  }

  const rows = users.data ?? [];
  const facilityById = new Map((facilities.data ?? []).map((f) => [f.facilityId, f]));

  return (
    <div className="mx-auto max-w-6xl px-6 py-8">
      <div className="mb-6 flex flex-wrap items-end justify-between gap-4 rise-in">
        <div>
          <p className="text-[10px] uppercase tracking-[0.3em] text-[color:var(--color-accent)]">
            Admin
          </p>
          <h1 className="text-3xl mt-1" style={{ fontFamily: "var(--font-display)" }}>
            Users<span className="caret-blink text-[color:var(--color-accent)]">▍</span>
          </h1>
          <p className="text-sm text-[color:var(--color-muted-fg)] mt-1 max-w-2xl">
            Local accounts are managed here; Novarad accounts appear automatically at first
            sign-in and keep their Novarad profile.
          </p>
        </div>
        <Button size="sm" onClick={() => setEditing(editing === "new" ? null : "new")}>
          <Plus className="size-4" />
          New local user
        </Button>
      </div>

      {editing === "new" ? (
        <UserForm
          mode="create"
          me={me.role}
          facilities={facilities.data ?? []}
          initial={EMPTY_FORM}
          onDone={(msg) => {
            setEditing(null);
            if (msg) setNote(msg);
            void invalidate();
          }}
        />
      ) : null}

      {note ? <p className="mb-4 text-sm text-[color:var(--color-accent)]">{note}</p> : null}

      {users.isLoading ? (
        <div className="min-h-[30vh] flex items-center justify-center">
          <Spinner size={24} />
        </div>
      ) : users.isError ? (
        <p className="text-sm text-[color:var(--color-novarad-red)]">
          Couldn&apos;t load users.{" "}
          <button className="underline underline-offset-2" onClick={() => users.refetch()}>
            Try again
          </button>
        </p>
      ) : (
        <ul className="space-y-2">
          {rows.map((u) => (
            <li key={u.userId}>
              <UserRow
                user={u}
                meId={me.userId}
                meRole={me.role}
                facilityById={facilityById}
                busy={
                  (toggleMut.isPending && toggleMut.variables?.id === u.userId) ||
                  (revokeMut.isPending && revokeMut.variables === u.userId)
                }
                onToggle={() => toggleMut.mutate({ id: u.userId, isActive: !u.isActive })}
                onRevoke={() => revokeMut.mutate(u.userId)}
                onEdit={() => setEditing(editing === u.userId ? null : u.userId)}
              />
              {editing === u.userId ? (
                <UserForm
                  mode="edit"
                  userId={u.userId}
                  me={me.role}
                  facilities={facilities.data ?? []}
                  initial={{
                    username: u.username,
                    displayName: u.displayName,
                    email: u.email ?? "",
                    role: u.role,
                    password: "",
                    facilityIds: u.facilityIds,
                  }}
                  lockRole={u.userId === me.userId}
                  onDone={(msg) => {
                    setEditing(null);
                    if (msg) setNote(msg);
                    void invalidate();
                  }}
                />
              ) : null}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function errText(err: unknown, fallback: string): string {
  const ax = err as AxiosError<{ error?: string }>;
  return ax.response?.data?.error ?? fallback;
}

function UserRow({
  user: u,
  meId,
  meRole,
  facilityById,
  busy,
  onToggle,
  onRevoke,
  onEdit,
}: {
  user: AdminUser;
  meId: string;
  meRole: Role | string;
  facilityById: Map<number, FacilityAdmin>;
  busy: boolean;
  onToggle: () => void;
  onRevoke: () => void;
  onEdit: () => void;
}) {
  const mayManage = isNrs(meRole) || u.role !== "NRS";
  const isSelf = u.userId === meId;
  const facilityNames = u.facilityIds
    .map((id) => facilityById.get(id)?.code ?? `#${id}`)
    .join(", ");

  return (
    <div className="flex flex-wrap items-center gap-x-3 gap-y-2 rounded-lg border border-[color:var(--color-border)] bg-[color:var(--color-surface)] px-4 py-2.5 transition-[border-color] hover:border-[color:var(--color-accent)]/40">
      <UserRound className="size-4 shrink-0 text-[color:var(--color-accent)]" />
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-2">
          <span className="font-medium" style={{ fontFamily: "var(--font-display)" }}>
            {u.username}
          </span>
          <Badge variant="accent">{u.role}</Badge>
          <Badge variant="neutral">{u.isLocal ? "local" : "Novarad"}</Badge>
          {u.isActive ? null : <Badge variant="caution">deactivated</Badge>}
          {isSelf ? <Badge variant="neutral">you</Badge> : null}
        </div>
        <p className="mt-0.5 text-xs text-[color:var(--color-muted-fg)] truncate">
          {u.displayName}
          {u.email ? ` · ${u.email}` : ""}
          {facilityNames ? ` · ${facilityNames}` : ""}
          {u.lastLoginAt ? ` · last sign-in ${formatDateTime(u.lastLoginAt)}` : " · never signed in"}
        </p>
      </div>

      <div className="flex items-center gap-1.5">
        {busy ? <Spinner size={14} /> : null}
        {u.activeSessionCount > 0 && mayManage ? (
          <Button
            variant="ghost"
            size="sm"
            onClick={onRevoke}
            disabled={busy}
            title={`${u.activeSessionCount} active sign-in(s) — sign out everywhere`}
          >
            <LogOut className="size-3.5" />
            {u.activeSessionCount}
          </Button>
        ) : null}
        {u.isLocal && mayManage ? (
          <Button variant="ghost" size="sm" onClick={onEdit} disabled={busy}>
            <Pencil className="size-3.5" />
            Edit
          </Button>
        ) : null}
        {mayManage && !isSelf ? (
          <Button
            variant={u.isActive ? "ghost" : "secondary"}
            size="sm"
            onClick={onToggle}
            disabled={busy}
          >
            {u.isActive ? "Deactivate" : "Reactivate"}
          </Button>
        ) : null}
      </div>
    </div>
  );
}

function UserForm({
  mode,
  userId,
  me,
  facilities,
  initial,
  lockRole,
  onDone,
}: {
  mode: "create" | "edit";
  userId?: string;
  me: Role | string;
  facilities: FacilityAdmin[];
  initial: UserFormState;
  lockRole?: boolean;
  onDone: (message: string | null) => void;
}) {
  const [form, setForm] = useState<UserFormState>(initial);
  const [error, setError] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const roles = ROLES.filter((r) => r !== "NRS" || isNrs(me));
  const selectableFacilities = facilities.filter(
    (f) => f.isActive || initial.facilityIds.includes(f.facilityId),
  );

  function patch(p: Partial<UserFormState>) {
    setForm((f) => ({ ...f, ...p }));
  }

  const save = useMutation({
    mutationFn: async () => {
      if (mode === "create") {
        return usersApi.create({
          username: form.username.trim(),
          displayName: form.displayName.trim(),
          email: form.email.trim() ? form.email.trim() : null,
          role: form.role,
          password: form.password,
          facilityIds: form.facilityIds,
        });
      }
      const updated = await usersApi.update(userId!, {
        displayName: form.displayName.trim(),
        email: form.email.trim() ? form.email.trim() : null,
        role: form.role,
        facilityIds: form.facilityIds,
      });
      if (form.password) await usersApi.setPassword(userId!, form.password);
      return updated;
    },
    onSuccess: (u) =>
      onDone(
        mode === "create"
          ? `${u.username} created.`
          : `${u.username} saved${form.password ? " — password reset, their sign-ins were revoked" : ""}.`,
      ),
    onError: (err) => setError(errText(err, "Couldn't save the user.")),
  });

  function submit() {
    if (mode === "create" && !form.username.trim()) {
      setError("username is required.");
      return;
    }
    if (!form.displayName.trim()) {
      setError("Display name is required.");
      return;
    }
    if (mode === "create" && form.password.length < 12) {
      setError("Password must be at least 12 characters — use the dice to generate one.");
      return;
    }
    if (mode === "edit" && form.password && form.password.length < 12) {
      setError("A new password must be at least 12 characters.");
      return;
    }
    setError(null);
    save.mutate();
  }

  return (
    <div className="mb-4 mt-1 space-y-4 rounded-lg border border-[color:var(--color-accent)]/40 bg-[color:var(--color-surface)] px-4 py-4 rise-in">
      <div className="flex items-center justify-between">
        <h2 className="text-sm font-medium" style={{ fontFamily: "var(--font-display)" }}>
          {mode === "create" ? "New local user" : `Edit ${initial.username}`}
        </h2>
        <button
          type="button"
          onClick={() => onDone(null)}
          aria-label="Close form"
          className="rounded p-1 text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-base-fg)]"
        >
          <X className="size-4" />
        </button>
      </div>

      <div className="grid gap-4 sm:grid-cols-2">
        {mode === "create" ? (
          <Field label="Username" required>
            <input
              value={form.username}
              onChange={(e) => patch({ username: e.target.value })}
              placeholder="e.g. admin.jane"
              className={inputCls}
            />
          </Field>
        ) : null}
        <Field label="Display name" required>
          <input
            value={form.displayName}
            onChange={(e) => patch({ displayName: e.target.value })}
            className={inputCls}
          />
        </Field>
        <Field label="Email">
          <input
            value={form.email}
            onChange={(e) => patch({ email: e.target.value })}
            placeholder="Optional"
            className={inputCls}
          />
        </Field>
        <Field label="Role" required hint={lockRole ? "You can't change your own role." : null}>
          <select
            value={form.role}
            onChange={(e) => patch({ role: e.target.value as Role })}
            disabled={lockRole}
            className={inputCls}
          >
            {roles.map((r) => (
              <option key={r} value={r}>
                {r}
              </option>
            ))}
          </select>
        </Field>
        <Field
          label={mode === "create" ? "Password" : "New password (blank = unchanged)"}
          required={mode === "create"}
          hint="At least 12 characters. Resetting a password signs the user out everywhere."
        >
          <div className="flex items-center gap-1.5">
            <input
              type="text"
              value={form.password}
              onChange={(e) => patch({ password: e.target.value })}
              autoComplete="new-password"
              spellCheck={false}
              className={`${inputCls} font-mono`}
            />
            <button
              type="button"
              title="Generate a strong password"
              aria-label="Generate a strong password"
              onClick={() => {
                patch({ password: generatePassword() });
                setCopied(false);
              }}
              className="inline-flex size-9 shrink-0 items-center justify-center rounded-md border border-[color:var(--color-border)] text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-accent)] hover:bg-[color:var(--color-surface-2)] transition-colors"
            >
              <Dices className="size-4" />
            </button>
            {form.password ? (
              <button
                type="button"
                title="Copy password"
                aria-label="Copy password"
                onClick={() => {
                  void navigator.clipboard.writeText(form.password).then(() => setCopied(true));
                }}
                className={`inline-flex h-9 shrink-0 items-center justify-center rounded-md border border-[color:var(--color-border)] px-2.5 text-xs transition-colors ${
                  copied
                    ? "text-[color:var(--color-success)]"
                    : "text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-accent)] hover:bg-[color:var(--color-surface-2)]"
                }`}
              >
                <KeyRound className="size-3.5 mr-1" />
                {copied ? "Copied" : "Copy"}
              </button>
            ) : null}
          </div>
        </Field>
        <Field label="Facilities" hint="Which facilities this user works at.">
          <div className="flex max-h-28 flex-wrap gap-x-4 gap-y-1 overflow-y-auto rounded-md border border-[color:var(--color-border)] px-2.5 py-2">
            {selectableFacilities.length === 0 ? (
              <span className="text-xs text-[color:var(--color-muted-fg)]">No facilities yet.</span>
            ) : (
              selectableFacilities.map((f) => (
                <label
                  key={f.facilityId}
                  className="inline-flex items-center gap-1.5 text-xs cursor-pointer select-none"
                >
                  <input
                    type="checkbox"
                    checked={form.facilityIds.includes(f.facilityId)}
                    onChange={(e) =>
                      patch({
                        facilityIds: e.target.checked
                          ? [...form.facilityIds, f.facilityId]
                          : form.facilityIds.filter((id) => id !== f.facilityId),
                      })
                    }
                    className="size-3.5 accent-[color:var(--color-accent)]"
                  />
                  {f.code}
                </label>
              ))
            )}
          </div>
        </Field>
      </div>

      {error ? <p className="text-sm text-[color:var(--color-novarad-red)]">{error}</p> : null}

      <div className="flex items-center gap-2">
        <Button size="sm" loading={save.isPending} onClick={submit}>
          {mode === "create" ? "Create user" : "Save changes"}
        </Button>
        <Button variant="ghost" size="sm" onClick={() => onDone(null)}>
          Cancel
        </Button>
      </div>
    </div>
  );
}
