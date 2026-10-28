"use client";

import { Plug } from "lucide-react";
import { useAgentsOnline } from "../hooks/use-agents-online";
import { Badge } from "./badge";
import { Skeleton } from "./skeleton";

// Live count of the tenant's online agents. Hidden when the API is unreachable; the health pill already says so.
export function AgentsPill() {
  const { isPending, isError, online = 0 } = useAgentsOnline();

  if (isPending) {
    return <Skeleton className="h-6 w-28" />;
  }
  if (isError) {
    return null;
  }

  return (
    <Badge tone={online > 0 ? "success" : "warning"} className="tabular-nums">
      <Plug className="size-3.5" aria-hidden />
      {online} {online === 1 ? "agent" : "agents"} online
    </Badge>
  );
}
