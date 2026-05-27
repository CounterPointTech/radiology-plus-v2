import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from "@microsoft/signalr";

import { API_BASE_URL } from "./api-client";
import { getAccessToken } from "./token-store";

const HUB_BASE =
  process.env.NEXT_PUBLIC_SIGNALR_URL?.replace(/\/$/, "") ??
  `${API_BASE_URL}/hubs`;

export function buildMonitoringConnection(): HubConnection {
  return new HubConnectionBuilder()
    .withUrl(`${HUB_BASE}/monitoring`, {
      // The JwtBearer setup on the server reads ?access_token=... for
      // SignalR-attached connections.
      accessTokenFactory: () => getAccessToken() ?? "",
    })
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build();
}

export async function safeStop(connection: HubConnection): Promise<void> {
  if (
    connection.state !== HubConnectionState.Disconnected &&
    connection.state !== HubConnectionState.Disconnecting
  ) {
    try {
      await connection.stop();
    } catch {
      // Stopping during reconnect can throw; ignore.
    }
  }
}
