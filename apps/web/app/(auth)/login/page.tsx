"use client";

import { AxiosError } from "axios";
import type { Route } from "next";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useEffect, useState } from "react";

import { Button } from "@/components/ui/button";
import { Card, CardBody, CardHeader, CardSubtitle, CardTitle } from "@/components/ui/card";
import { Input, Label } from "@/components/ui/input";
import { Spinner } from "@/components/ui/spinner";
import { canVisit, roleHome } from "@/lib/access";
import { useAuth } from "@/lib/auth-context";
import type { Role } from "@/lib/types";

function safeNext(raw: string | null): string | null {
  if (!raw) return null;
  try {
    const decoded = decodeURIComponent(raw);
    // Only allow same-origin paths.
    if (decoded.startsWith("/") && !decoded.startsWith("//")) return decoded;
  } catch {
    // fall through
  }
  return null;
}

// Send the user to the requested page when their role may visit it, otherwise
// to their role's home — one hop, no bounce through the shell guard.
function destinationFor(role: Role | string, rawNext: string | null): Route {
  const next = safeNext(rawNext);
  if (next && canVisit(role, next)) return next as Route;
  return roleHome(role);
}

// useSearchParams() forces a client-side bailout, so the production build requires
// it behind a Suspense boundary — LoginPage (the route entry) provides one.
function LoginForm() {
  const router = useRouter();
  const params = useSearchParams();
  const { login, user, isAuthenticated, isHydrated } = useAuth();

  const [facility, setFacility] = useState("AHC");
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const rawNext = params.get("next");

  useEffect(() => {
    if (isHydrated && isAuthenticated && user) {
      router.replace(destinationFor(user.role, rawNext));
    }
  }, [isHydrated, isAuthenticated, user, router, rawNext]);

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
