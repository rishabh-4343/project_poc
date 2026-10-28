"use client";

import { useHealth } from "../hooks/use-health";
import { Badge } from "./badge";
import { Skeleton } from "./skeleton";

// Reports whether the backend answers /health; the "agents online" pill replaces it once agents are listed.
export function HealthPill() {
  const { isPending, isError } = useHealth();

  if (isPending) {
    return <Skeleton className="h-6 w-24" />;
  }

  return isError ? (
    <Badge tone="danger">
      <span className="size-1.5 rounded-full bg-danger" aria-hidden />
      API offline
    </Badge>
  ) : (
    <Badge tone="success">
      <span className="size-1.5 rounded-full bg-success" aria-hidden />
      API online
    </Badge>
  );
}
