import { get, post } from "./api-client";
import { apiClient } from "./api-client";
import type {
  NotificationsStatus,
  ScriptCancelResult,
  ScriptDetail,
  ScriptExecutionDetail,
  ScriptExecutionListItem,
  ScriptSaveRequest,
  ScriptSmokeTestResult,
  ScriptSummary,
  ScriptVersionDetail,
  ScriptVersionInfo,
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

  /** Notifications scaffold status (NRS/Admin). */
  notificationsStatus() {
    return get<NotificationsStatus>("/notifications/status");
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
