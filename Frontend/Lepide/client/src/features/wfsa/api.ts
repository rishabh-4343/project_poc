import { api, download } from "@/shared/api";
import type {
  ActionFilters,
  ActionPage,
  Agent,
  Dashboard,
  IntegrityEvent,
  NewSession,
  RegisteredAgent,
  Server,
  Session,
  Share,
} from "./types";

function filterParams(f: ActionFilters) {
  const p = new URLSearchParams();
  if (f.operation) p.set("operation", f.operation);
  if (f.actor) p.set("actor", f.actor);
  if (f.success !== undefined) p.set("success", String(f.success));
  return p;
}

export const wfsaApi = {
  agents: () => api<Agent[]>("/api/agents"),
  registerAgent: (name: string) => api<RegisteredAgent>("/api/agents", { method: "POST", body: { name } }),
  revokeAgent: (id: string) => api<void>(`/api/agents/${id}/revoke`, { method: "POST" }),

  servers: () => api<Server[]>("/api/servers"),
  registerServer: (body: { agentId: string; hostName: string; osVersion?: string }) =>
    api<Server>("/api/servers", { method: "POST", body }),
  approveServer: (id: string) => api<Server>(`/api/servers/${id}/approve`, { method: "POST" }),

  shares: () => api<Share[]>("/api/shares"),
  registerShare: (body: { serverId: string; name: string; localPath: string; uncPath: string }) =>
    api<Share>("/api/shares", { method: "POST", body }),

  sessions: () => api<Session[]>("/api/wfsa/sessions"),
  session: (id: string) => api<Session>(`/api/wfsa/sessions/${id}`),
  createSession: (body: NewSession) => api<Session>("/api/wfsa/sessions", { method: "POST", body }),
  stopSession: (id: string) => api<Session>(`/api/wfsa/sessions/${id}/stop`, { method: "POST" }),

  actions: (id: string, filters: ActionFilters, before?: string) => {
    const p = filterParams(filters);
    p.set("limit", "200");
    if (before) p.set("before", before);
    return api<ActionPage>(`/api/wfsa/sessions/${id}/actions?${p}`);
  },
  exportCsv: (id: string, filters: ActionFilters) =>
    download(`/api/wfsa/sessions/${id}/export.csv?${filterParams(filters)}`, `session-${id}.csv`),

  dashboard: (days: number) => api<Dashboard>(`/api/wfsa/dashboard?days=${days}`),

  integrityEvents: (limit = 10) => api<IntegrityEvent[]>(`/api/wfsa/integrity-events?limit=${limit}`),
  sessionIntegrity: (id: string) => api<IntegrityEvent[]>(`/api/wfsa/sessions/${id}/integrity-events`),
};
