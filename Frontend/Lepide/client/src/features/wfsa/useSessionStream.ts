"use client";

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useInfiniteQuery, useQueryClient } from "@tanstack/react-query";
import { createUiConnection } from "@/shared/signalr";
import { wfsaApi } from "./api";
import { keys } from "./hooks";
import type { ActionFilters, ActionRow, Session, SessionUpdate } from "./types";

const CAP = 2000;
const FRESH_MS = 1500;

const rowKey = (r: ActionRow) => `${r.serverId}:${r.seq}`;

function matches(r: ActionRow, f: ActionFilters) {
  return (
    (!f.operation || f.operation.split(",").includes(r.operation)) &&
    (f.success === undefined || r.success === f.success) &&
    (!f.actor ||
      (r.actorName ?? "").toLowerCase().includes(f.actor.toLowerCase()) ||
      r.actorSid === f.actor)
  );
}

// History comes from the keyset endpoint; new rows arrive on the hub and are merged on top,
// deduplicated by (server, seq), newest first, and capped so a long session cannot exhaust memory.
export function useSessionStream(sessionId: string, filters: ActionFilters) {
  const qc = useQueryClient();
  const filtersRef = useRef(filters);
  const filterKey = JSON.stringify(filters);
  // Live rows belong to one filter set; a different set starts empty, so nothing needs resetting.
  const [liveState, setLiveState] = useState<{ key: string; rows: ActionRow[] }>({ key: filterKey, rows: [] });
  const live = liveState.key === filterKey ? liveState.rows : [];
  const [fresh, setFresh] = useState<Set<string>>(new Set());
  const [connected, setConnected] = useState(false);

  const history = useInfiniteQuery({
    queryKey: ["wfsa", "actions", sessionId, filters],
    queryFn: ({ pageParam }) => wfsaApi.actions(sessionId, filters, pageParam),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });

  useEffect(() => {
    filtersRef.current = filters;
  }, [filters]);

  useEffect(() => {
    const connection = createUiConnection();
    const timers: ReturnType<typeof setTimeout>[] = [];

    const join = () =>
      connection
        .invoke("JoinSession", sessionId)
        .then(() => setConnected(true))
        .catch(() => setConnected(false));

    connection.on("ActionsAppended", (id: string, rows: ActionRow[]) => {
      if (id !== sessionId) return;
      const wanted = rows.filter((r) => matches(r, filtersRef.current));
      if (wanted.length === 0) return;

      const key = JSON.stringify(filtersRef.current);
      setLiveState((prev) => {
        const base = prev.key === key ? prev.rows : [];
        const seen = new Set(base.map(rowKey));
        return { key, rows: [...wanted.filter((r) => !seen.has(rowKey(r))), ...base].slice(0, CAP) };
      });

      const keysAdded = wanted.map(rowKey);
      setFresh((prev) => new Set([...prev, ...keysAdded]));
      timers.push(
        setTimeout(() => setFresh((prev) => new Set([...prev].filter((k) => !keysAdded.includes(k)))), FRESH_MS),
      );
    });

    connection.on("SessionUpdated", (update: SessionUpdate) => {
      if (update.id !== sessionId) return;
      qc.setQueryData<Session>(keys.session(sessionId), (old) => (old ? { ...old, ...update } : old));
    });

    connection.onreconnecting(() => setConnected(false));
    connection.onreconnected(() => {
      // Rows pushed while the socket was down are in the keyset history, so re-fetch rather than guess.
      join();
      history.refetch();
    });
    connection.onclose(() => setConnected(false));

    connection.start().then(join).catch(() => setConnected(false));

    return () => {
      timers.forEach(clearTimeout);
      connection.stop();
    };
    // history.refetch is stable enough for this one-shot reconnect handler
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sessionId, qc]);

  const rows = useMemo(() => {
    const seen = new Set<string>();
    const merged: ActionRow[] = [];
    for (const r of [...live, ...(history.data?.pages.flatMap((p) => p.items) ?? [])]) {
      const k = rowKey(r);
      if (!seen.has(k)) {
        seen.add(k);
        merged.push(r);
      }
    }
    merged.sort((a, b) => b.occurredAt.localeCompare(a.occurredAt) || b.seq - a.seq);
    return merged.slice(0, CAP);
  }, [live, history.data]);

  const loadOlder = useCallback(() => history.fetchNextPage(), [history]);

  return {
    rows,
    fresh,
    connected,
    isLoading: history.isPending,
    isError: history.isError,
    error: history.error,
    hasMore: history.hasNextPage,
    loadingMore: history.isFetchingNextPage,
    loadOlder,
  };
}
