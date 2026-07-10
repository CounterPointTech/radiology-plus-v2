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

export interface ScriptSmokeTestResult {
  ok: boolean;
  status: string;
  durationMs: number;
  output: string | null;
  error: string | null;
}

// ---------------------------------------------------------------------------
// Script Manager
// ---------------------------------------------------------------------------

export type ScriptLanguageToken = "tsql" | "pgsql" | "powershell" | "batch";
export type ConnectionTargetToken = "appdb" | "novarad" | "mmodal" | "none";
export type ScriptRunStatus = "pending" | "running" | "success" | "failed" | "cancelled";

export const LANGUAGE_LABEL: Record<ScriptLanguageToken, string> = {
  pgsql: "PostgreSQL",
  tsql: "SQL Server",
  powershell: "PowerShell",
  batch: "Batch",
};

export const TARGET_LABEL: Record<ConnectionTargetToken, string> = {
  appdb: "Radiology Plus DB",
  novarad: "Novarad",
  mmodal: "M*Modal",
  none: "No connection",
};

/** Which connection targets each language may use (mirrors server validation). */
export const TARGETS_FOR_LANGUAGE: Record<ScriptLanguageToken, ConnectionTargetToken[]> = {
  pgsql: ["appdb", "novarad"],
  tsql: ["mmodal"],
  powershell: ["none"],
  batch: ["none"],
};

export interface ScriptSummary {
  scriptId: string;
  name: string;
  description: string | null;
  language: ScriptLanguageToken;
  connectionTarget: ConnectionTargetToken;
  cronExpression: string | null;
  nextRunAt: string | null;
  isActive: boolean;
  timeoutSeconds: number;
  createdAt: string;
  updatedAt: string;
  lastExecutionId: number | null;
  lastStatus: ScriptRunStatus | null;
  lastStartedAt: string | null;
  lastDurationMs: number | null;
}

export interface ScriptDetail {
  scriptId: string;
  name: string;
  description: string | null;
  language: ScriptLanguageToken;
  body: string;
  connectionTarget: ConnectionTargetToken;
  cronExpression: string | null;
  nextRunAt: string | null;
  isActive: boolean;
  timeoutSeconds: number;
  parameters: Record<string, unknown> | null;
  createdBy: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface ScriptSaveRequest {
  name: string;
  description?: string | null;
  language: ScriptLanguageToken;
  body: string;
  connectionTarget: ConnectionTargetToken;
  cronExpression?: string | null;
  isActive: boolean;
  timeoutSeconds?: number;
  parameters?: Record<string, unknown> | null;
}

export interface ScriptExecutionListItem {
  executionId: number;
  scriptId: string;
  scriptName: string;
  triggeredBy: string;
  status: ScriptRunStatus;
  startedAt: string | null;
  completedAt: string | null;
  durationMs: number | null;
  rowsAffected: number | null;
  createdAt: string;
}

export interface ScriptExecutionDetail extends ScriptExecutionListItem {
  exitCode: number | null;
  outputLog: string | null;
  errorLog: string | null;
}

export interface ScriptVersionInfo {
  versionId: string;
  scriptId: string;
  versionNumber: number;
  bodyChars: number;
  savedBy: string | null;
  savedAt: string;
}

export interface ScriptVersionDetail {
  versionId: string;
  scriptId: string;
  versionNumber: number;
  body: string;
  savedBy: string | null;
  savedAt: string;
}

export interface ScriptCancelResult {
  cancelled: boolean;
  message: string;
}

export interface NotificationsStatus {
  scaffold: boolean;
  message: string;
}
