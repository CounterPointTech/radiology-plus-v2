// Mirror of the .NET DTOs served by RadiologyPlus.AdminApi. Keep in sync when
// the contract changes. (Copied surface from apps/web — promote to a shared
// workspace package when divergence warrants.)

// Role is serialized as the C# enum name ("NRS" | "Admin" | "Tech" | "Radiologist").
export type Role = "NRS" | "Admin" | "Tech" | "Radiologist";

export function roleLabel(role: Role | string): string {
  switch (role) {
    case "NRS":
      return "NRS";
    case "Admin":
      return "Admin";
    case "Tech":
      return "Tech";
    case "Radiologist":
      return "Radiologist";
    default:
      return role;
  }
}

export function isNrs(role: Role | string): boolean {
  return role === "NRS";
}

// Whole-app gate: the technical console is for NRS/Admin only.
export function canAccessAdmin(role: Role | string): boolean {
  return role === "NRS" || role === "Admin";
}

// Script Manager is NRS-only (mirrors Role.CanAccessScripting server-side).
export function canAccessScripting(role: Role | string): boolean {
  return role === "NRS";
}

// ---------------------------------------------------------------------------
// Auth
// ---------------------------------------------------------------------------

export interface AuthUser {
  userId: string;
  username: string;
  displayName: string | null;
  email: string | null;
  role: Role;
  facilityIds: number[];
}

export interface AuthResponse {
  accessToken: string;
  refreshToken: string;
  expiresInSeconds: number;
  user: AuthUser;
}

export interface LoginRequest {
  facility: string;
  username: string;
  password: string;
}

// ---------------------------------------------------------------------------
// AdminApi scaffold DTOs
// ---------------------------------------------------------------------------

export interface VersionInfo {
  product: string;
  version: string;
  time: string;
}

export interface ScriptsStatus {
  scaffold: boolean;
  message: string;
  supportedLanguages: string[];
}

export interface ScriptSmokeTestResult {
  ok: boolean;
  status: string;
  durationMs: number;
  output: string | null;
  error: string | null;
}

export interface NotificationsStatus {
  scaffold: boolean;
  message: string;
}
