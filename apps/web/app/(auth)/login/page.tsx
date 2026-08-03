"use client";

import { AxiosError } from "axios";
import type { Route } from "next";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";

import { Button } from "@/components/ui/button";
import { Card, CardBody, CardHeader, CardSubtitle, CardTitle } from "@/components/ui/card";
import { Input, Label } from "@/components/ui/input";
import { Spinner } from "@/components/ui/spinner";
import { canVisit, roleHome } from "@/lib/access";
import { useAuth } from "@/lib/auth-context";
import type { Role } from "@/lib/types";

// A host that can never resolve (RFC 2606 reserves .invalid), used only as the base
// for parsing. Resolving the candidate with the URL parser applies exactly the rules
// the browser will apply, which prefix checks cannot: "/\host" and "/\/\host" look
// like paths but resolve as network-path references to another host, so the old
// `startsWith("/") && !startsWith("//")` test let them through and shipped the user
// off-origin. A fixed sentinel keeps this SSR-safe (no `window`) and independent of
// whichever port the app is served on.
const SAME_ORIGIN_BASE = "http://radiology-plus.invalid";

/** The requested path, but only when it cannot leave this origin. */
function safeNext(raw: string | null): string | null {
  if (!raw) return null;
  try {
    const url = new URL(decodeURIComponent(raw), SAME_ORIGIN_BASE);
    // Anything carrying its own host, scheme or credentials resolves elsewhere.
    if (url.origin !== SAME_ORIGIN_BASE) return null;
    const path = `${url.pathname}${url.search}${url.hash}`;
    return path.startsWith("/") && !path.startsWith("//") ? path : null;
  } catch {
    // Malformed escape sequence or unparseable URL — treat as no destination.
    return null;
  }
}

// Send the user to the requested page when their role may visit it, otherwise
// to their role's home — one hop, no bounce through the shell guard.
function destinationFor(role: Role | string, rawNext: string | null): Route {
  const next = safeNext(rawNext);
  // Match the role rules on the path alone; a query string is not part of the route.
  if (next && canVisit(role, next.split(/[?#]/)[0])) return next as Route;
  return roleHome(role);
}

// useSearchParams() forces a client-side bailout, so the production build requires
// it behind a Suspense boundary — LoginPage (the route entry) provides one.
function LoginForm() {
  const router = useRouter();
  const params = useSearchParams();
  const { login, logout, user, isAuthenticated, isHydrated } = useAuth();

  const [facility, setFacility] = useState("AHC");
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [switching, setSwitching] = useState(false);

  const rawNext = params.get("next");

  // Arriving here with a live session used to redirect silently, so anyone opening
  // /login to change users was bounced back as whoever was already signed in —
  // and keystrokes typed before hydration finished went nowhere. Ask instead.
  const alreadySignedIn = isHydrated && isAuthenticated && !!user && !switching;

  async function handleSwitchUser() {
    setSwitching(true);
    await logout();
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    setSubmitting(true);
    try {
      const authUser = await login({ facility, username, password });
      router.replace(destinationFor(authUser.role, rawNext));
    } catch (err) {
      const ax = err as AxiosError<{ message?: string }>;
      if (ax.response?.status === 401) {
        setError("Sign-in failed. Check your facility code, username, and password.");
      } else if (ax.code === "ECONNABORTED" || ax.message?.includes("Network")) {
        setError("Couldn't reach the API. Is the service running?");
      } else {
        setError(ax.response?.data?.message ?? "Unexpected sign-in error.");
      }
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <main className="min-h-screen flex items-center justify-center px-4 py-10">
      <div className="w-full max-w-md">
        <div className="mb-6 text-center">
          <p className="text-[10px] uppercase tracking-[0.3em] text-[color:var(--color-accent)]">
            Radiology Plus
          </p>
          <h1
            className="mt-2 text-3xl"
            style={{ fontFamily: "var(--font-display)" }}
          >
            Sign in
          </h1>
        </div>

        <Card>
          <CardHeader>
            <CardTitle>Welcome back</CardTitle>
            <CardSubtitle>
              Use your NovaRad account, or your NRS credentials for admin tasks.
            </CardSubtitle>
          </CardHeader>
          <CardBody>
            {alreadySignedIn ? (
              <div className="space-y-4">
                <p className="text-sm">
                  You&apos;re already signed in as{" "}
                  <span className="font-medium">
                    {user!.displayName || user!.username}
                  </span>{" "}
                  <span className="text-[color:var(--color-muted-fg)]">
                    ({user!.role})
                  </span>
                  .
                </p>
                <div className="flex gap-2">
                  <Button
                    onClick={() => router.replace(destinationFor(user!.role, rawNext))}
                  >
                    Continue
                  </Button>
                  <Button variant="secondary" onClick={handleSwitchUser}>
                    Sign in as someone else
                  </Button>
                </div>
              </div>
            ) : (
            <form onSubmit={handleSubmit} className="space-y-4">
              <div className="space-y-1.5">
                <Label htmlFor="facility" required>
                  Facility
                </Label>
                <Input
                  id="facility"
                  autoComplete="organization"
                  value={facility}
                  onChange={(e) => setFacility(e.target.value)}
                  placeholder="AHC"
                  required
                />
              </div>
              <div className="space-y-1.5">
                <Label htmlFor="username" required>
                  Username
                </Label>
                <Input
                  id="username"
                  autoComplete="username"
                  value={username}
                  onChange={(e) => setUsername(e.target.value)}
                  required
                  autoFocus
                />
              </div>
              <div className="space-y-1.5">
                <Label htmlFor="password" required>
                  Password
                </Label>
                <Input
                  id="password"
                  type="password"
                  autoComplete="current-password"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  required
                />
              </div>

              {error ? (
                <div
                  role="alert"
                  className="rounded-md border border-[color:var(--color-novarad-red)]/40 bg-[color:var(--color-novarad-red)]/10 px-3 py-2 text-sm text-[color:var(--color-novarad-red)]"
                >
                  {error}
                </div>
              ) : null}

              <Button
                type="submit"
                size="lg"
                className="w-full"
                loading={submitting}
              >
                {submitting ? "Signing in" : "Sign in"}
              </Button>
            </form>
            )}
          </CardBody>
        </Card>

        <p className="mt-6 text-center text-xs text-[color:var(--color-muted-fg)]">
          <span className="inline-block w-1.5 h-1.5 rounded-full bg-[color:var(--color-novarad-red)] mr-1.5 align-middle" />
          A NovaRad / iPro product.
        </p>
      </div>
    </main>
  );
}

export default function LoginPage() {
  return (
    <Suspense
      fallback={
        <main className="min-h-screen flex items-center justify-center">
          <Spinner size={28} />
        </main>
      }
    >
      <LoginForm />
    </Suspense>
  );
}
