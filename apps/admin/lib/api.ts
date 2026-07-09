import { get, post } from "./api-client";
import type {
  NotificationsStatus,
  ScriptSmokeTestResult,
  ScriptsStatus,
  VersionInfo,
} from "./types";

/** Typed wrappers over the AdminApi scaffold endpoints. */
export const adminApi = {
  /** Unauthenticated connectivity probe. */
  version() {
    return get<VersionInfo>("/diagnostics/version");
  },

  /** Script Manager scaffold status (NRS only). */
  scriptsStatus() {
    return get<ScriptsStatus>("/scripts/status");
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
