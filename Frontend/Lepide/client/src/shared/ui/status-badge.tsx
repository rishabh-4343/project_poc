import * as React from "react";
import { Badge, type BadgeTone } from "./badge";
import { cn } from "../cn";

// One place that decides badge color from meaning, so no screen restyles badges ad hoc.
const operationTone: Record<string, BadgeTone> = {
  Created: "success",
  Modified: "info",
  Deleted: "danger",
  Renamed: "accent",
  Moved: "accent",
  Copied: "accent",
  PermissionChange: "warning",
  OwnerChange: "warning",
  AuditingChange: "warning",
  Open: "neutral",
  Read: "info",
  ContentView: "info",
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

type Kind = "operation" | "session" | "access";

type StatusBadgeProps = Omit<React.ComponentProps<"span">, "children"> & {
  kind: Kind;
  value: string | boolean;
};

export function StatusBadge({ kind, value, className, ...props }: StatusBadgeProps) {
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
      {label.replace(/([a-z])([A-Z])/g, "$1 $2")}
    </Badge>
  );
}
