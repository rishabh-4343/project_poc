"use client";

import { useHealth } from "../hooks/use-health";
import { Badge } from "./badge";
import { Skeleton } from "./skeleton";

// Reports whether the backend answers /health; AgentsPill sits beside it with the online agent count.
export function HealthPill() {
  const { isPending, isError } = useHealth();

  if (isPending) {
    return <Skeleton className="h-6 w-24" />;
  }

  return isError ? (
    <Badge tone="danger">
      <span className="size-1.5 rounded-full bg-danger" aria-hidden />
      Service offline
    </Badge>
  ) : (
    <Badge tone="success">
      <span className="size-1.5 rounded-full bg-success" aria-hidden />
      Service online
    </Badge>
  );
}
