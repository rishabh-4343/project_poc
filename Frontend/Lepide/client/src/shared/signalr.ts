import { HubConnectionBuilder, LogLevel, type HubConnection, type ILogger } from "@microsoft/signalr";
import { API_URL } from "./config";

// Warnings and errors go to the console, except the abort logged when an effect stops a connection that is still
// negotiating (React runs effects twice in development); callers already handle a failed start.
const logger: ILogger = {
  log(level, message) {
    if (level < LogLevel.Warning || message.includes("stopped during negotiation")) return;
    (level >= LogLevel.Error ? console.error : console.warn)(`[signalr] ${message}`);
  },
};

// A fresh connection to /hubs/ui. The caller owns start/stop and registers its own handlers
// (ActionsAppended, SessionUpdated); under DevAuth the hub needs no token.
export function createUiConnection(): HubConnection {
  return new HubConnectionBuilder()
    .withUrl(`${API_URL}/hubs/ui`)
    .withAutomaticReconnect()
    .configureLogging(logger)
    .build();
}
