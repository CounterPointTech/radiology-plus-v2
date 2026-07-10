"use client";

import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
} from "react";

import { apiClient, get, post } from "./api-client";
import {
  getAuth,
  setAuth as storeAuth,
  subscribe,
  type StoredAuth,
} from "./token-store";
import type {
  AuthResponse,
  AuthUser,
  LoginRequest,
} from "./types";

interface AuthContextValue {
  user: AuthUser | null;
  accessToken: string | null;
  isAuthenticated: boolean;
  isHydrated: boolean;
  login: (req: LoginRequest) => Promise<AuthUser>;
  logout: () => Promise<void>;
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [auth, setAuthState] = useState<StoredAuth | null>(null);
  const [isHydrated, setIsHydrated] = useState(false);

  // Hydrate from localStorage and validate the token against /auth/me on mount.
  // This avoids the "looks logged in until first 401" jank when refresh-token
  // rotation has expired the stored session.
  useEffect(() => {
    const stored = getAuth();
    setAuthState(stored);

    let cancelled = false;
    async function validate() {
      if (!stored) {
        if (!cancelled) setIsHydrated(true);
        return;
      }
      try {
        // The axios 401 interceptor will try /auth/refresh once before this throws.
        const fresh = await get<AuthUser>("/auth/me");
        if (cancelled) return;
        // Refresh-flow may have rotated tokens; pull the latest from the store
        // rather than reusing `stored`.
        const latest = getAuth();
        if (latest) {
          const next: StoredAuth = { ...latest, user: fresh };
          storeAuth(next);
        }
      } catch {
        if (cancelled) return;
        storeAuth(null);
      } finally {
        if (!cancelled) setIsHydrated(true);
      }
    }
    void validate();

    const unsubscribe = subscribe((next) => {
      if (!cancelled) setAuthState(next);
    });
    return () => {
      cancelled = true;
      unsubscribe();
    };
  }, []);

  const login = useCallback(async (req: LoginRequest) => {
    const data = await apiClient.post<AuthResponse>("/auth/login", req, {
      skipAuth: true,
    });
    const next: StoredAuth = {
      accessToken: data.data.accessToken,
      refreshToken: data.data.refreshToken,
      user: data.data.user,
    };
    storeAuth(next);
    return data.data.user;
  }, []);

  const logout = useCallback(async () => {
    try {
      await post("/auth/logout");
    } catch {
      // Even if the server roundtrip fails, clear local state.
    } finally {
      storeAuth(null);
    }
  }, []);

  const value = useMemo<AuthContextValue>(
    () => ({
      user: auth?.user ?? null,
      accessToken: auth?.accessToken ?? null,
      isAuthenticated: !!auth,
      isHydrated,
      login,
      logout,
    }),
    [auth, isHydrated, login, logout],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be used inside <AuthProvider>");
  return ctx;
}
