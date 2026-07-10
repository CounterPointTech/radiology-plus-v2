// Single source of truth for auth tokens shared between AuthContext and the
// axios refresh interceptor. AuthContext owns the React state; the axios
// interceptor reads/writes here so it doesn't have to live inside React.

import type { AuthUser } from "./types";

const ACCESS_KEY = "radplus.accessToken";
const REFRESH_KEY = "radplus.refreshToken";
const USER_KEY = "radplus.user";

export interface StoredAuth {
  accessToken: string;
  refreshToken: string;
  user: AuthUser;
}

type Listener = (auth: StoredAuth | null) => void;
const listeners = new Set<Listener>();

let memoryAccess: string | null = null;
let memoryRefresh: string | null = null;
let memoryUser: AuthUser | null = null;

function read(): void {
  if (typeof window === "undefined") return;
  memoryAccess = window.localStorage.getItem(ACCESS_KEY);
  memoryRefresh = window.localStorage.getItem(REFRESH_KEY);
  const rawUser = window.localStorage.getItem(USER_KEY);
  memoryUser = rawUser ? (JSON.parse(rawUser) as AuthUser) : null;
}

let hydrated = false;
function ensureHydrated(): void {
  if (!hydrated) {
    read();
    hydrated = true;
  }
}

export function getAccessToken(): string | null {
  ensureHydrated();
  return memoryAccess;
}

export function getRefreshToken(): string | null {
  ensureHydrated();
  return memoryRefresh;
}

export function getUser(): AuthUser | null {
  ensureHydrated();
  return memoryUser;
}

export function getAuth(): StoredAuth | null {
  ensureHydrated();
  if (!memoryAccess || !memoryRefresh || !memoryUser) return null;
  return {
    accessToken: memoryAccess,
    refreshToken: memoryRefresh,
    user: memoryUser,
  };
}

export function setAuth(auth: StoredAuth | null): void {
  memoryAccess = auth?.accessToken ?? null;
  memoryRefresh = auth?.refreshToken ?? null;
  memoryUser = auth?.user ?? null;
  hydrated = true;
  if (typeof window !== "undefined") {
    if (auth) {
      window.localStorage.setItem(ACCESS_KEY, auth.accessToken);
      window.localStorage.setItem(REFRESH_KEY, auth.refreshToken);
      window.localStorage.setItem(USER_KEY, JSON.stringify(auth.user));
    } else {
      window.localStorage.removeItem(ACCESS_KEY);
      window.localStorage.removeItem(REFRESH_KEY);
      window.localStorage.removeItem(USER_KEY);
    }
  }
  for (const l of listeners) l(auth);
}

export function subscribe(listener: Listener): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}
