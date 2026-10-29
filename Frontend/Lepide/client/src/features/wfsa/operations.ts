// The one place operation names become words. Used by filters, the operation column and report labels.
const OP_LABELS: Record<string, string> = {
  Created: "Created",
  Modified: "Modified",
  Deleted: "Deleted",
  Renamed: "Renamed",
  Moved: "Moved",
  Copied: "Copied",
  Open: "Open",
  Read: "Read",
  ReadBasicInfo: "Read (Basic Information)",
  ContentView: "Content View",
  PropertyChanged: "Property Changed",
  PermissionChange: "Security Changed (Permissions)",
  OwnerChange: "Security Changed (Owner)",
  AuditingChange: "Security Changed (Auditing)",
  ShareCreated: "Share Created",
  ShareRemoved: "Share Removed",
  Other: "Other",
};

// Share operations name their object themselves, so they never take a File/Folder prefix.
const SHARE_OPS = new Set(["ShareCreated", "ShareRemoved"]);

// The operation alone, e.g. "Security Changed (Permissions)" — for filter options and audit settings.
export function operationName(operation: string) {
  return OP_LABELS[operation] ?? operation.replace(/([a-z])([A-Z])/g, "$1 $2");
}

// The full event label: "{File|Folder} {Operation}{ (Denied)}", e.g. "Folder Deleted (Denied)".
export function eventLabel(operation: string, objectType: string, success: boolean) {
  const prefix = SHARE_OPS.has(operation) || !["File", "Folder", "Share"].includes(objectType) ? "" : `${objectType} `;
  return `${prefix}${operationName(operation)}${success ? "" : " (Denied)"}`;
}
