import * as React from "react";
import { Badge, type BadgeTone } from "./badge";
import { cn } from "../cn";

// One place that decides badge color from meaning, so no screen restyles badges ad hoc. Semantic colors only:
// creations green, deletions red, edits and reads blue, security changes amber; moves and renames stay neutral.
const operationTone: Record<string, BadgeTone> = {
  Created: "success",
  Modified: "info",
  Deleted: "danger",
  Renamed: "neutral",
  Moved: "neutral",
  Copied: "neutral",
  PropertyChanged: "info",
  PermissionChange: "warning",
  OwnerChange: "warning",
  AuditingChange: "warning",
  Open: "neutral",
  Read: "info",
  ReadBasicInfo: "info",
  ContentView: "info",
  ShareCreated: "success",
  ShareRemoved: "danger",
};

const sessionTone: Record<string, BadgeTone> = {
  Requested: "neutral",
  Starting: "info",
  Active: "success",
  Degraded: "warning",
  Stopping: "warning",
  Completed: "neutral",
  Failed: "danger",
};

// Audit states as the user reads them.
const sessionLabel: Record<string, string> = { Requested: "Pending", Degraded: "Needs attention" };

type Kind = "operation" | "session" | "access";

type StatusBadgeProps = Omit<React.ComponentProps<"span">, "children"> & {
  kind: Kind;
  value: string | boolean;
  // Display text when the caller has a better name than the raw value (e.g. the operation display names).
  label?: string;
};

export function StatusBadge({ kind, value, label: text, className, ...props }: StatusBadgeProps) {
  if (kind === "access") {
    const allowed = value === true;
    return (
      <Badge tone={allowed ? "success" : "danger"} className={className} {...props}>
        {allowed ? "Allowed" : "Denied"}
      </Badge>
    );
  }

  const label = String(value);
  const tone = (kind === "operation" ? operationTone : sessionTone)[label] ?? "neutral";

  return (
    <Badge tone={tone} className={cn(className)} {...props}>
      {text ?? (kind === "session" ? sessionLabel[label] : undefined) ?? label.replace(/([a-z])([A-Z])/g, "$1 $2")}
    </Badge>
  );
}
