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

// ---------------------------------------------------------------------------
// Admin pages: users / facilities / settings / audit
// ---------------------------------------------------------------------------

export const ROLES: Role[] = ["NRS", "Admin", "Tech", "Radiologist"];

export interface AdminUser {
  userId: string;
  username: string;
  displayName: string;
  email: string | null;
  role: Role;
  isLocal: boolean;
  isActive: boolean;
  lastLoginAt: string | null;
  createdAt: string;
  facilityIds: number[];
  activeSessionCount: number;
}

export interface UserCreateRequest {
  username: string;
  displayName: string;
  email?: string | null;
  role: Role;
  password: string;
  facilityIds: number[];
}

export interface UserUpdateRequest {
  displayName: string;
  email?: string | null;
  role: Role;
  facilityIds: number[];
}

export interface UserSession {
  tokenId: string;
  createdAt: string;
  expiresAt: string;
}

export interface FacilityAdmin {
  facilityId: number;
  novaradFacilityId: number;
  code: string;
  displayName: string;
  isActive: boolean;
  createdAt: string;
  userCount: number;
}

export interface FacilitySaveRequest {
  novaradFacilityId: number;
  code: string;
  displayName: string;
  isActive: boolean;
}

export interface FacilityImportResult {
  inserted: number;
  updated: number;
  total: number;
}

export interface TenantInfo {
  code: string;
  displayName: string;
  isActive: boolean;
}

export interface NovaradConnection {
  host: string;
  port: number;
  database: string;
  username: string;
  useSsl: boolean;
  novaradAuditTable: string;
  notes: string | null;
  hasPassword: boolean;
  updatedAt: string;
}

export interface NovaradConnectionResponse {
  configured: boolean;
  settings: NovaradConnection | null;
}

export interface NovaradConnectionSaveRequest {
  host: string;
  port: number;
  database: string;
  username: string;
  password?: string | null;
  useSsl: boolean;
  novaradAuditTable?: string | null;
  notes?: string | null;
}

export interface NovaradTestResult {
  ok: boolean;
  durationMs: number;
  serverVersion: string | null;
  error: string | null;
}

export type AuditActionToken =
  | "Login"
  | "Logout"
  | "Read"
  | "Create"
  | "Update"
  | "Delete"
  | "Execute"
  | "NovaradWrite"
  | "PermissionDenied"
  | "MModalWrite";

export const AUDIT_ACTIONS: AuditActionToken[] = [
  "Login",
  "Logout",
  "Read",
  "Create",
  "Update",
  "Delete",
  "Execute",
  "NovaradWrite",
  "PermissionDenied",
  "MModalWrite",
];

export interface AuditLogItem {
  logId: number;
  userId: string | null;
  username: string | null;
  action: AuditActionToken;
  resourceType: string;
  resourceId: string | null;
  success: boolean;
  ipAddress: string | null;
  userAgent: string | null;
  errorMessage: string | null;
  metadataJson: string | null;
  occurredAt: string;
}

export interface AuditLogPage {
  items: AuditLogItem[];
  total: number;
}

// ---------------------------------------------------------------------------
// Script chains
// ---------------------------------------------------------------------------

export type ChainOnFailure = "stop" | "continue";
/** Chain runs share the script run-status tokens. */
export type ChainRunStatus = ScriptRunStatus;

export interface ChainSummary {
  chainId: string;
  name: string;
  description: string | null;
  onFailure: ChainOnFailure;
  cronExpression: string | null;
  nextRunAt: string | null;
  isActive: boolean;
  stepCount: number;
  notifiesOnFailure: boolean;
  createdAt: string;
  lastRunId: number | null;
  lastRunStatus: ChainRunStatus | null;
  lastRunStartedAt: string | null;
  lastRunDurationMs: number | null;
}

export interface ChainStep {
  stepOrder: number;
  scriptId: string;
  scriptName: string;
  language: ScriptLanguageToken;
  scriptIsActive: boolean;
  continueOnFailure: boolean;
}

export interface ChainDetail {
  chainId: string;
  name: string;
  description: string | null;
  onFailure: ChainOnFailure;
  cronExpression: string | null;
  nextRunAt: string | null;
  isActive: boolean;
  notifyOnFailureRecipient: string | null;
  notifyOnFailureTemplateId: string | null;
  createdAt: string;
  steps: ChainStep[];
}

export interface ChainStepSaveRequest {
  scriptId: string;
  continueOnFailure: boolean;
}

export interface ChainSaveRequest {
  name: string;
  description?: string | null;
  onFailure: ChainOnFailure;
  cronExpression?: string | null;
  isActive: boolean;
  notifyOnFailureRecipient?: string | null;
  notifyOnFailureTemplateId?: string | null;
  steps: ChainStepSaveRequest[];
}

export interface ChainRunInfo {
  chainRunId: number;
  chainId: string;
  chainName: string;
  triggeredBy: string;
  status: ChainRunStatus;
  startedAt: string | null;
  completedAt: string | null;
  durationMs: number | null;
  stepsTotal: number;
  stepsSucceeded: number;
  stepsFailed: number;
  errorSummary: string | null;
  createdAt: string;
}

export interface ChainRunStep {
  executionId: number;
  scriptId: string;
  scriptName: string;
  status: ScriptRunStatus;
  startedAt: string | null;
  completedAt: string | null;
  durationMs: number | null;
  rowsAffected: number | null;
}

export interface ChainRunDetail {
  run: ChainRunInfo;
  steps: ChainRunStep[];
}

export interface ChainCancelResult {
  cancelled: boolean;
  message: string;
}

// ---------------------------------------------------------------------------
// Notifications console
// ---------------------------------------------------------------------------

export type NotificationChannelToken = "email" | "teams" | "sms" | "webhook";
export type NotificationStatusToken =
  | "pending"
  | "sending"
  | "sent"
  | "failed"
  | "cancelled";

export const NOTIFICATION_CHANNELS: NotificationChannelToken[] = [
  "email",
  "teams",
  "sms",
  "webhook",
];

export const NOTIFICATION_STATUSES: NotificationStatusToken[] = [
  "pending",
  "sending",
  "sent",
  "failed",
  "cancelled",
];

export const CHANNEL_LABEL: Record<NotificationChannelToken, string> = {
  email: "Email",
  teams: "Teams",
  sms: "SMS",
  webhook: "Webhook",
};

/** Channels with a live sender today; the rest queue but fail delivery. */
export const LIVE_CHANNELS: NotificationChannelToken[] = ["email"];

export interface NotificationQueueItem {
  notificationId: number;
  templateId: string | null;
  templateName: string | null;
  channel: NotificationChannelToken;
  recipient: string;
  subject: string | null;
  priority: number;
  status: NotificationStatusToken;
  retryCount: number;
  maxRetries: number;
  scheduledAt: string;
  sentAt: string | null;
  failedAt: string | null;
  lastError: string | null;
  sourceType: string | null;
  sourceId: string | null;
  createdAt: string;
}

export interface NotificationQueueDetail extends NotificationQueueItem {
  body: string;
  isHtml: boolean;
}

export interface NotificationQueuePage {
  items: NotificationQueueItem[];
  total: number;
}

export interface NotificationQueueActionResult {
  changed: boolean;
  item: NotificationQueueDetail;
  message: string;
}

export interface NotificationChannelCount {
  channel: NotificationChannelToken;
  count: number;
}

export interface NotificationStats {
  pending: number;
  sending: number;
  sent24h: number;
  failed: number;
  oldestPendingAt: string | null;
  byChannel24h: NotificationChannelCount[];
}

export interface NotificationComposeRequest {
  channel?: NotificationChannelToken;
  recipient: string;
  subject?: string | null;
  body?: string | null;
  isHtml: boolean;
  priority?: number;
  templateId?: string | null;
  variables?: Record<string, unknown> | null;
}

export interface NotificationTemplateSummary {
  templateId: string;
  name: string;
  channel: NotificationChannelToken;
  isHtml: boolean;
  isActive: boolean;
  createdAt: string;
}

export interface NotificationTemplateDetail extends NotificationTemplateSummary {
  subjectTemplate: string | null;
  bodyTemplate: string;
}

export interface NotificationTemplateSaveRequest {
  name: string;
  channel: NotificationChannelToken;
  subjectTemplate?: string | null;
  bodyTemplate: string;
  isHtml: boolean;
  isActive: boolean;
}

export interface TemplatePreviewResult {
  subject: string | null;
  body: string;
}

export interface GraphEmailSettings {
  graphTenantId: string;
  clientId: string;
  hasClientSecret: boolean;
  fromAddress: string;
  updatedAt: string;
}

export interface GraphEmailSettingsResponse {
  configured: boolean;
  settings: GraphEmailSettings | null;
}

export interface GraphEmailSettingsSaveRequest {
  graphTenantId: string;
  clientId: string;
  clientSecret?: string | null;
  fromAddress: string;
}

export interface GraphTestResult {
  ok: boolean;
  error: string | null;
  sentTestEmail: boolean;
}
