export type Agent = {
  id: string;
  name: string;
  online: boolean;
  status: string;
  lastSeenAt: string | null;
  agentVersion: string | null;
  driverVersion: string | null;
};

export type Server = {
  id: string;
  agentId: string;
  hostName: string;
  osVersion: string | null;
  approved: boolean;
  createdAt: string;
};

export type Share = {
  id: string;
  serverId: string;
  name: string;
  localPath: string;
  uncPath: string;
  approved: boolean;
};

export type SessionState =
  | "Requested"
  | "Starting"
  | "Active"
  | "Degraded"
  | "Stopping"
  | "Completed"
  | "Failed";

export type Folder = { shareId: string | null; localPath: string; recursive: boolean };

export type Session = {
  id: string;
  serverId: string;
  agentId: string;
  name: string | null;
  operations: string[];
  includeReads: boolean;
  state: SessionState;
  startedBy: string;
  stoppedBy: string | null;
  requestedAt: string;
  activeAt: string | null;
  stoppedAt: string | null;
  maxEndAt: string | null;
  failureReason: string | null;
  folders: Folder[];
};

// Pushed on /hubs/ui as SessionUpdated; merged into a Session by id.
export type SessionUpdate = Pick<Session, "id" | "serverId" | "state" | "failureReason" | "activeAt" | "stoppedAt" | "stoppedBy">;

export type ActionRow = {
  serverId: string;
  seq: number;
  occurredAt: string;
  objectType: "File" | "Folder" | "Share";
  operation: string;
  success: boolean;
  certainty: "Certain" | "Inferred";
  path: string;
  newPath: string | null;
  shareName: string | null;
  actorSid: string;
  actorName: string | null;
  clientIp: string | null;
  processName: string | null;
  contentType: string;
  riskLevel: number;
  complianceTags: string[];
  monetaryValue: number;
  details: string | null;
};

export type ActionPage = { items: ActionRow[]; nextCursor: string | null };

export type ActionFilters = {
  operation?: string;
  actor?: string;
  success?: boolean;
};

export type Dashboard = {
  days: number;
  eventsToday: number;
  deniedToday: number;
  activeSessions: number;
  perDay: { day: string; actions: number; denied: number }[];
  byOperation: { operation: string; actions: number }[];
  topActors: { actorSid: string; actions: number }[];
};

export type NewSession = {
  serverId: string;
  name?: string;
  operations: string[];
  includeReads: boolean;
  folders: { localPath: string; shareId?: string; recursive: boolean }[];
};

// Operations grouped for the New session form; reads are a separate switch because of their volume.
export const OPERATION_GROUPS = [
  { label: "Changes", operations: ["Created", "Modified", "Deleted", "Renamed", "Moved", "Copied"] },
  { label: "Security", operations: ["PermissionChange", "OwnerChange", "AuditingChange"] },
  { label: "Other", operations: ["PropertyChanged", "ShareCreated", "ShareRemoved", "Other"] },
] as const;

export const DEFAULT_OPERATIONS = ["Created", "Modified", "Deleted", "Renamed", "PermissionChange", "OwnerChange"];

export const STOPPABLE: SessionState[] = ["Requested", "Starting", "Active", "Degraded"];

export type IntegrityKind =
  | "DriverOverflow"
  | "Gap"
  | "GapRecovered"
  | "AgentOffline"
  | "DriverUnloaded"
  | "TamperSuspected";

export type IntegrityEvent = {
  id: number;
  serverId: string;
  kind: IntegrityKind;
  startedAt: string;
  endedAt: string | null;
  firstSeq: number | null;
  lastSeq: number | null;
  // jsonb as text: { droppedCount, message }
  details: string | null;
};

export type RegisteredAgent = { id: string; name: string; apiKey: string };
