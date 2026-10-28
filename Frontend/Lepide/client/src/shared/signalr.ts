import { HubConnectionBuilder, LogLevel, type HubConnection } from "@microsoft/signalr";
import { API_URL } from "./config";

// A fresh connection to /hubs/ui. The caller owns start/stop and registers its own handlers
// (ActionsAppended, SessionUpdated); under DevAuth the hub needs no token.
export function createUiConnection(): HubConnection {
  return new HubConnectionBuilder()
    .withUrl(`${API_URL}/hubs/ui`)
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build();
}
