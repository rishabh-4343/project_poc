"use client";

import { useEffect } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "../api";
import { createUiConnection } from "../signalr";

type AgentPresence = { id: string; online: boolean };

const key = ["agents", "presence"] as const;

// The tenant's agents, seeded from /api/agents and kept live by the AgentUpdated push every UI connection receives.
export function useAgentsOnline() {
  const qc = useQueryClient();
  const query = useQuery({ queryKey: key, queryFn: () => api<AgentPresence[]>("/api/agents"), refetchInterval: 60_000 });

  useEffect(() => {
    const connection = createUiConnection();

    connection.on("AgentUpdated", (update: AgentPresence) => {
      qc.setQueryData<AgentPresence[]>(key, (old) =>
        old && (old.some((a) => a.id === update.id) ? old.map((a) => (a.id === update.id ? { ...a, ...update } : a)) : [...old, update]),
      );
    });
    // The tenant group also carries session updates; this connection has no use for them.
    connection.on("SessionUpdated", () => {});
    connection.onreconnected(() => qc.invalidateQueries({ queryKey: key }));

    connection.start().catch(() => {
      // Offline: the query's own polling keeps the count roughly right until the hub reconnects.
    });

    return () => {
      connection.stop();
    };
  }, [qc]);

  return { ...query, online: query.data?.filter((a) => a.online).length };
}
