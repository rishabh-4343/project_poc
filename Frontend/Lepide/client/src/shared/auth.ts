// DevAuth stub: one fixed tenant and user, matching the backend's seeded dev identity.
// Swap this module for real auth later; callers only use getSession() and authHeaders().
export type Session = {
  tenantId: string;
  userId: string;
  displayName: string;
  email: string;
  role: "admin" | "auditor" | "viewer";
};

const devSession: Session = {
  tenantId: "00000000-0000-0000-0000-000000000001",
  userId: "00000000-0000-0000-0000-000000000002",
  displayName: "Dev Admin",
  email: "dev@auditor.local",
  role: "admin",
};

export function getSession(): Session {
  return devSession;
}

// The backend identifies the tenant itself under DevAuth, so no header is needed yet.
export function authHeaders(): Record<string, string> {
  return {};
}
