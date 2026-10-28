"use client";

import { useEffect } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { createUiConnection } from "@/shared/signalr";
import { wfsaApi } from "./api";
import type { Agent, Session, SessionUpdate } from "./types";

export const keys = {
  agents: ["wfsa", "agents"] as const,
  servers: ["wfsa", "servers"] as const,
  shares: ["wfsa", "shares"] as const,
  sessions: ["wfsa", "sessions"] as const,
  session: (id: string) => ["wfsa", "session", id] as const,
  dashboard: (days: number) => ["wfsa", "dashboard", days] as const,
  integrity: ["wfsa", "integrity"] as const,
  sessionIntegrity: (id: string) => ["wfsa", "integrity", "session", id] as const,
};

export const useAgents = () =>
  useQuery({ queryKey: keys.agents, queryFn: wfsaApi.agents, refetchInterval: 10_000 });

export const useIntegrityEvents = (limit = 8) =>
  useQuery({ queryKey: [...keys.integrity, limit], queryFn: () => wfsaApi.integrityEvents(limit), refetchInterval: 15_000 });

export const useSessionIntegrity = (id: string) =>
  useQuery({ queryKey: keys.sessionIntegrity(id), queryFn: () => wfsaApi.sessionIntegrity(id), refetchInterval: 15_000 });

export const useServers = () => useQuery({ queryKey: keys.servers, queryFn: wfsaApi.servers });

export const useShares = () => useQuery({ queryKey: keys.shares, queryFn: wfsaApi.shares });

export const useSessions = () => useQuery({ queryKey: keys.sessions, queryFn: wfsaApi.sessions });

export const useSession = (id: string) =>
  useQuery({ queryKey: keys.session(id), queryFn: () => wfsaApi.session(id) });

export const useDashboard = (days = 14) =>
  useQuery({ queryKey: keys.dashboard(days), queryFn: () => wfsaApi.dashboard(days), refetchInterval: 15_000 });

export function useRegisterServer() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: wfsaApi.registerServer,
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.servers }),
  });
}

export function useApproveServer() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: wfsaApi.approveServer,
    onSuccess: () => {
      toast.success("Server approved");
      return qc.invalidateQueries({ queryKey: keys.servers });
    },
    onError: (e) => toast.error(e.message),
  });
}

export function useRegisterShare() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: wfsaApi.registerShare,
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.shares }),
  });
}

export function useCreateSession() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: wfsaApi.createSession,
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.sessions }),
  });
}

export function useStopSession() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: wfsaApi.stopSession,
    onSuccess: (session) => {
      qc.setQueryData(keys.session(session.id), session);
      return qc.invalidateQueries({ queryKey: keys.sessions });
    },
    onError: (e) => toast.error(e.message),
  });
}

// Folds a SessionUpdated push into every cached copy of that session.
export function useSessionUpdates() {
  const qc = useQueryClient();

  useEffect(() => {
    const connection = createUiConnection();

    connection.on("SessionUpdated", (update: SessionUpdate) => {
      qc.setQueryData<Session>(keys.session(update.id), (old) => (old ? { ...old, ...update } : old));
      qc.setQueryData<Session[]>(keys.sessions, (old) =>
        old?.map((s) => (s.id === update.id ? { ...s, ...update } : s)),
      );
      qc.invalidateQueries({ queryKey: ["wfsa", "dashboard"] });
    });
    // Presence changes arrive as AgentUpdated; an offline agent also opens an integrity event, so refresh those.
    connection.on("AgentUpdated", (update: Agent) => {
      qc.setQueryData<Agent[]>(keys.agents, (old) => old?.map((a) => (a.id === update.id ? { ...a, ...update } : a)));
      qc.invalidateQueries({ queryKey: keys.integrity });
    });
    connection.onreconnected(() => {
      qc.invalidateQueries({ queryKey: keys.sessions });
      qc.invalidateQueries({ queryKey: keys.agents });
    });

    connection.start().catch(() => {
      // Offline: the pages still poll or refetch on focus; the hub reconnects on its own schedule.
    });

    return () => {
      connection.stop();
    };
  }, [qc]);
}

export function useRegisterAgent() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: wfsaApi.registerAgent,
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.agents }),
  });
}

export function useRevokeAgent() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: wfsaApi.revokeAgent,
    onSuccess: () => {
      toast.success("Agent revoked");
      return qc.invalidateQueries({ queryKey: keys.agents });
    },
    onError: (e) => toast.error(e.message),
  });
}
