import axios, {
  AxiosError,
  type AxiosInstance,
  type AxiosResponse,
  type InternalAxiosRequestConfig,
} from "axios";

import {
  getAccessToken,
  getRefreshToken,
  setAuth,
  type StoredAuth,
} from "./token-store";
import type { AuthResponse } from "./types";

export const API_BASE_URL =
  process.env.NEXT_PUBLIC_API_URL?.replace(/\/$/, "") ??
  "http://localhost:5171";

export const apiClient: AxiosInstance = axios.create({
  baseURL: API_BASE_URL,
  headers: { "Content-Type": "application/json" },
  // Cancel slow requests early so a broken VPN tunnel doesn't hang the UI.
  timeout: 30_000,
});

apiClient.interceptors.request.use((config) => {
  if (config.skipAuth) return config;
  const token = getAccessToken();
  if (token) {
    config.headers.set("Authorization", `Bearer ${token}`);
  }
  return config;
});

// Refresh-token rotation: when /auth/refresh succeeds the old refresh token
// is revoked, so concurrent 401s must share a single in-flight refresh
// promise. Otherwise the second caller would burn the rotated token.
let pendingRefresh: Promise<StoredAuth | null> | null = null;

async function refreshTokens(): Promise<StoredAuth | null> {
  const refreshToken = getRefreshToken();
  if (!refreshToken) return null;
  try {
    const res = await axios.post<AuthResponse>(
      `${API_BASE_URL}/auth/refresh`,
      { refreshToken },
      { headers: { "Content-Type": "application/json" }, timeout: 15_000 },
    );
    const next: StoredAuth = {
      accessToken: res.data.accessToken,
      refreshToken: res.data.refreshToken,
      user: res.data.user,
    };
    setAuth(next);
    return next;
  } catch {
    setAuth(null);
    return null;
  }
}

apiClient.interceptors.response.use(
  (r) => r,
  async (error: AxiosError) => {
    const original = error.config as
      | (InternalAxiosRequestConfig & { _retried?: boolean })
      | undefined;
    // A server Forbid means the signed-in role lacks the capability. Give the
    // page's error rendering plain language instead of "status code 403".
    if (error.response?.status === 403) {
      error.message = "You don't have permission to do that.";
      return Promise.reject(error);
    }
    if (!original || error.response?.status !== 401 || original._retried) {
      return Promise.reject(error);
    }
    // Don't try to refresh on auth endpoints — a 401 there is the real answer.
    const url = original.url ?? "";
    if (url.startsWith("/auth/login") || url.startsWith("/auth/refresh")) {
      return Promise.reject(error);
    }
    original._retried = true;
    pendingRefresh ??= refreshTokens().finally(() => {
      pendingRefresh = null;
    });
    const refreshed = await pendingRefresh;
    if (!refreshed) {
      if (typeof window !== "undefined") {
        const next = encodeURIComponent(
          window.location.pathname + window.location.search,
        );
        window.location.assign(`/login?next=${next}`);
      }
      return Promise.reject(error);
    }
    original.headers.set("Authorization", `Bearer ${refreshed.accessToken}`);
    return apiClient.request(original);
  },
);

// Convenience typed wrappers ------------------------------------------------

export async function get<T>(url: string, params?: Record<string, unknown>) {
  const res: AxiosResponse<T> = await apiClient.get(url, { params });
  return res.data;
}

export async function post<T>(url: string, body?: unknown) {
  const res: AxiosResponse<T> = await apiClient.post(url, body);
  return res.data;
}

/** GET that returns the response body as a Blob plus the server-suggested filename
 *  parsed from Content-Disposition (when present). Used for spreadsheet downloads. */
export async function getBlob(
  url: string,
  params?: Record<string, unknown>,
): Promise<{ blob: Blob; filename: string | null }> {
  const res = await apiClient.get<Blob>(url, { params, responseType: "blob" });
  const disp = (res.headers as Record<string, string>)["content-disposition"] ?? "";
  const match =
    /filename\*=UTF-8''([^;]+)/i.exec(disp) ?? /filename="?([^";]+)"?/i.exec(disp);
  const filename = match ? decodeURIComponent(match[1]!.trim()) : null;
  return { blob: res.data, filename };
}

declare module "axios" {
  // Allow callers to flag a request as "do not attach the bearer token".
  export interface AxiosRequestConfig {
    skipAuth?: boolean;
  }
  export interface InternalAxiosRequestConfig {
    skipAuth?: boolean;
  }
}
