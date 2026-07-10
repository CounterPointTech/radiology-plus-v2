import { get, post } from "./api-client";
import { apiClient } from "./api-client";
import type {
  AdminUser,
  AuditActionToken,
  AuditLogPage,
  ChainCancelResult,
  ChainDetail,
  ChainRunDetail,
  ChainRunInfo,
  ChainSaveRequest,
  ChainSummary,
  FacilityAdmin,
  FacilityImportResult,
  FacilitySaveRequest,
  GraphEmailSettingsResponse,
  GraphEmailSettingsSaveRequest,
  GraphTestResult,
  NotificationChannelToken,
  NotificationComposeRequest,
  NotificationQueueActionResult,
  NotificationQueueDetail,
  NotificationQueuePage,
  NotificationStats,
  NotificationStatusToken,
  NotificationTemplateDetail,
  NotificationTemplateSaveRequest,
  NotificationTemplateSummary,
  NovaradConnectionResponse,
  NovaradConnectionSaveRequest,
  NovaradTestResult,
  ScriptCancelResult,
  ScriptDetail,
  ScriptExecutionDetail,
  ScriptExecutionListItem,
  ScriptSaveRequest,
  ScriptSmokeTestResult,
  ScriptSummary,
  ScriptVersionDetail,
  ScriptVersionInfo,
  TemplatePreviewResult,
  TenantInfo,
  UserCreateRequest,
  UserSession,
  UserUpdateRequest,
  VersionInfo,
} from "./types";

/** Typed wrappers over the AdminApi. */
export const adminApi = {
  /** Unauthenticated connectivity probe. */
  version() {
    return get<VersionInfo>("/diagnostics/version");
  },

  /** Runs an inline SELECT 1 through the real script executor (NRS only). */
  runScriptSmokeTest() {
    return post<ScriptSmokeTestResult>("/scripts/test");
  },
};

/** Users management (NRS/Admin, enforced server-side). */
export const usersApi = {
  list() {
    return get<AdminUser[]>("/users/");
  },
  create(body: UserCreateRequest) {
    return post<AdminUser>("/users/", body);
  },
  async update(userId: string, body: UserUpdateRequest) {
    const res = await apiClient.put<AdminUser>(`/users/${encodeURIComponent(userId)}`, body);
    return res.data;
  },
  async setActive(userId: string, isActive: boolean) {
    const res = await apiClient.patch<AdminUser>(
      `/users/${encodeURIComponent(userId)}/active`,
      { isActive },
    );
    return res.data;
  },
  setPassword(userId: string, password: string) {
    return post<{ ok: boolean; sessionsRevoked: number }>(
      `/users/${encodeURIComponent(userId)}/password`,
      { password },
    );
  },
  sessions(userId: string) {
    return get<UserSession[]>(`/users/${encodeURIComponent(userId)}/sessions`);
  },
  revokeSessions(userId: string) {
    return post<{ revoked: number }>(`/users/${encodeURIComponent(userId)}/sessions/revoke`);
  },
};

/** Facilities management (NRS/Admin, enforced server-side). */
export const facilitiesApi = {
  list() {
    return get<FacilityAdmin[]>("/facilities/");
  },
  create(body: FacilitySaveRequest) {
    return post<FacilityAdmin>("/facilities/", body);
  },
  async update(facilityId: number, body: FacilitySaveRequest) {
    const res = await apiClient.put<FacilityAdmin>(`/facilities/${facilityId}`, body);
    return res.data;
  },
  async remove(facilityId: number) {
    await apiClient.delete(`/facilities/${facilityId}`);
  },
  importFromNovarad() {
    return post<FacilityImportResult>("/facilities/import");
  },
};

/** Tenant settings (NRS/Admin, enforced server-side). */
export const settingsApi = {
  tenant() {
    return get<TenantInfo>("/settings/tenant");
  },
  novarad() {
    return get<NovaradConnectionResponse>("/settings/novarad");
  },
  async saveNovarad(body: NovaradConnectionSaveRequest) {
    const res = await apiClient.put<NovaradConnectionResponse>("/settings/novarad", body);
    return res.data;
  },
  async deleteNovarad() {
    await apiClient.delete("/settings/novarad");
  },
  testNovarad() {
    return post<NovaradTestResult>("/settings/novarad/test");
  },
};

/** Audit log viewer (NRS/Admin, enforced server-side). */
export const auditApi = {
  list(params?: {
    username?: string;
    action?: AuditActionToken;
    success?: boolean;
    from?: string;
    to?: string;
    limit?: number;
    offset?: number;
  }) {
    return get<AuditLogPage>("/audit/", params);
  },
};

/** Script Chains (NRS only, enforced server-side). */
export const chainsApi = {
  list() {
    return get<ChainSummary[]>("/chains/");
  },
  get(chainId: string) {
    return get<ChainDetail>(`/chains/${encodeURIComponent(chainId)}`);
  },
  create(body: ChainSaveRequest) {
    return post<ChainDetail>("/chains/", body);
  },
  async update(chainId: string, body: ChainSaveRequest) {
    const res = await apiClient.put<ChainDetail>(
      `/chains/${encodeURIComponent(chainId)}`,
      body,
    );
    return res.data;
  },
  async remove(chainId: string) {
    await apiClient.delete(`/chains/${encodeURIComponent(chainId)}`);
  },
  async setActive(chainId: string, isActive: boolean) {
    const res = await apiClient.patch<ChainDetail>(
      `/chains/${encodeURIComponent(chainId)}/active`,
      { isActive },
    );
    return res.data;
  },
  run(chainId: string) {
    return post<{ started: boolean; chainRunId: number }>(
      `/chains/${encodeURIComponent(chainId)}/run`,
    );
  },
  recentRuns(params?: { limit?: number }) {
    return get<ChainRunInfo[]>("/chains/runs", params);
  },
  runsFor(chainId: string, params?: { limit?: number }) {
    return get<ChainRunInfo[]>(`/chains/${encodeURIComponent(chainId)}/runs`, params);
  },
  runDetail(chainRunId: number) {
    return get<ChainRunDetail>(`/chains/runs/${chainRunId}`);
  },
  cancelRun(chainRunId: number) {
    return post<ChainCancelResult>(`/chains/runs/${chainRunId}/cancel`);
  },
};

/** Notifications console (NRS/Admin, enforced server-side). */
export const notificationsApi = {
  stats() {
    return get<NotificationStats>("/notifications/stats");
  },
  queue(params?: {
    status?: NotificationStatusToken;
    channel?: NotificationChannelToken;
    limit?: number;
    offset?: number;
  }) {
    return get<NotificationQueuePage>("/notifications/queue", params);
  },
  queueItem(notificationId: number) {
    return get<NotificationQueueDetail>(`/notifications/queue/${notificationId}`);
  },
  cancel(notificationId: number) {
    return post<NotificationQueueActionResult>(
      `/notifications/queue/${notificationId}/cancel`,
    );
  },
  retry(notificationId: number) {
    return post<NotificationQueueActionResult>(
      `/notifications/queue/${notificationId}/retry`,
    );
  },
  compose(body: NotificationComposeRequest) {
    return post<NotificationQueueDetail>("/notifications/compose", body);
  },

  templates() {
    return get<NotificationTemplateSummary[]>("/notifications/templates");
  },
  template(templateId: string) {
    return get<NotificationTemplateDetail>(
      `/notifications/templates/${encodeURIComponent(templateId)}`,
    );
  },
  createTemplate(body: NotificationTemplateSaveRequest) {
    return post<NotificationTemplateDetail>("/notifications/templates", body);
  },
  async updateTemplate(templateId: string, body: NotificationTemplateSaveRequest) {
    const res = await apiClient.put<NotificationTemplateDetail>(
      `/notifications/templates/${encodeURIComponent(templateId)}`,
      body,
    );
    return res.data;
  },
  async setTemplateActive(templateId: string, isActive: boolean) {
    const res = await apiClient.patch<NotificationTemplateDetail>(
      `/notifications/templates/${encodeURIComponent(templateId)}/active`,
      { isActive },
    );
    return res.data;
  },
  async deleteTemplate(templateId: string) {
    await apiClient.delete(`/notifications/templates/${encodeURIComponent(templateId)}`);
  },
  previewTemplate(body: {
    subjectTemplate?: string | null;
    bodyTemplate: string;
    variables?: Record<string, unknown> | null;
  }) {
    return post<TemplatePreviewResult>("/notifications/templates/preview", body);
  },

  graphSettings() {
    return get<GraphEmailSettingsResponse>("/notifications/settings/graph");
  },
  async saveGraphSettings(body: GraphEmailSettingsSaveRequest) {
    const res = await apiClient.put<GraphEmailSettingsResponse>(
      "/notifications/settings/graph",
      body,
    );
    return res.data;
  },
  async deleteGraphSettings() {
    await apiClient.delete("/notifications/settings/graph");
  },
  testGraphSettings(recipient?: string | null) {
    return post<GraphTestResult>("/notifications/settings/graph/test", {
      recipient: recipient ?? null,
    });
  },
};

/** Script Manager (NRS only, enforced server-side). */
export const scriptsApi = {
  list() {
    return get<ScriptSummary[]>("/scripts/");
  },
  get(scriptId: string) {
    return get<ScriptDetail>(`/scripts/${encodeURIComponent(scriptId)}`);
  },
  async create(body: ScriptSaveRequest) {
    const res = await apiClient.post<ScriptDetail>("/scripts/", body);
    return res.data;
  },
  async update(scriptId: string, body: ScriptSaveRequest) {
    const res = await apiClient.put<ScriptDetail>(`/scripts/${encodeURIComponent(scriptId)}`, body);
    return res.data;
  },
  async remove(scriptId: string) {
    await apiClient.delete(`/scripts/${encodeURIComponent(scriptId)}`);
  },
  async setActive(scriptId: string, isActive: boolean) {
    const res = await apiClient.patch<ScriptDetail>(
      `/scripts/${encodeURIComponent(scriptId)}/active`,
      { isActive },
    );
    return res.data;
  },
  async run(scriptId: string) {
    const res = await apiClient.post<{ started: boolean; scriptId: string }>(
      `/scripts/${encodeURIComponent(scriptId)}/run`,
    );
    return res.data;
  },
  recentExecutions(params?: { limit?: number }) {
    return get<ScriptExecutionListItem[]>("/scripts/executions", params);
  },
  executionsFor(scriptId: string, params?: { limit?: number }) {
    return get<ScriptExecutionListItem[]>(
      `/scripts/${encodeURIComponent(scriptId)}/executions`,
      params,
    );
  },
  execution(executionId: number) {
    return get<ScriptExecutionDetail>(`/scripts/executions/${executionId}`);
  },
  async cancel(executionId: number) {
    const res = await apiClient.post<ScriptCancelResult>(
      `/scripts/executions/${executionId}/cancel`,
    );
    return res.data;
  },
  versions(scriptId: string) {
    return get<ScriptVersionInfo[]>(`/scripts/${encodeURIComponent(scriptId)}/versions`);
  },
  version(versionId: string) {
    return get<ScriptVersionDetail>(`/scripts/versions/${encodeURIComponent(versionId)}`);
  },
};
