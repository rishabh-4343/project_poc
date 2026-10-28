"use client";

import { useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { AlertTriangle, ArrowLeft, Download, Square } from "lucide-react";
import { toast } from "sonner";
import { Badge } from "@/shared/ui/badge";
import { Button } from "@/shared/ui/button";
import { Dialog, DialogClose, DialogContent } from "@/shared/ui/dialog";
import { Input, Select } from "@/shared/ui/input";
import { LiveIndicator } from "@/shared/ui/live-indicator";
import { Skeleton } from "@/shared/ui/skeleton";
import { StatusBadge } from "@/shared/ui/status-badge";
import { ActionsTable } from "../ActionsTable";
import { IntegrityEvents } from "../IntegrityEvents";
import { wfsaApi } from "../api";
import { useServers, useSession, useSessionIntegrity, useStopSession } from "../hooks";
import { useSessionStream } from "../useSessionStream";
import { STOPPABLE, type ActionFilters } from "../types";

export function SessionPageSkeleton() {
  return (
    <div className="flex flex-col gap-6">
      <Skeleton className="h-8 w-64" />
      <Skeleton className="h-5 w-96" />
      <Skeleton className="h-96 w-full" />
    </div>
  );
}

export function SessionPage({ id }: { id: string }) {
  const session = useSession(id);
  const servers = useServers();
  const stop = useStopSession();
  const integrity = useSessionIntegrity(id);

  const [operation, setOperation] = useState("");
  const [access, setAccess] = useState("");
  const [actorInput, setActorInput] = useState("");
  const [actor, setActor] = useState("");
  const [confirmStop, setConfirmStop] = useState(false);
  const [exporting, setExporting] = useState(false);

  useEffect(() => {
    const t = setTimeout(() => setActor(actorInput.trim()), 300);
    return () => clearTimeout(t);
  }, [actorInput]);

  const filters = useMemo<ActionFilters>(
    () => ({
      operation: operation || undefined,
      actor: actor || undefined,
      success: access === "" ? undefined : access === "allowed",
    }),
    [operation, actor, access],
  );
  const filtered = Boolean(filters.operation || filters.actor || filters.success !== undefined);

  const stream = useSessionStream(id, filters);
  const serverNames = useMemo(() => new Map(servers.data?.map((s) => [s.id, s.hostName])), [servers.data]);

  if (session.isError) {
    return (
      <div className="flex flex-col items-start gap-4">
        <Button variant="ghost" asChild><Link href="/wfsa/sessions"><ArrowLeft aria-hidden />Sessions</Link></Button>
        <p className="text-sm text-danger" role="alert">{session.error.message}</p>
      </div>
    );
  }

  if (!session.data) {
    return <SessionPageSkeleton />;
  }

  const s = session.data;
  const recording = s.state === "Active" || s.state === "Degraded";

  async function exportCsv() {
    setExporting(true);
    try {
      await wfsaApi.exportCsv(id, filters);
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Export failed");
    } finally {
      setExporting(false);
    }
  }

  return (
    <div className="flex flex-col gap-5">
      <div className="flex flex-col gap-3">
        <Button variant="ghost" size="sm" asChild className="-ml-3 self-start">
          <Link href="/wfsa/sessions"><ArrowLeft aria-hidden />Sessions</Link>
        </Button>

        <div className="flex flex-wrap items-start justify-between gap-4">
          <div className="flex flex-col gap-2">
            <div className="flex flex-wrap items-center gap-3">
              <h1 className="text-[22px] font-semibold leading-tight tracking-tight">{s.name || "Untitled session"}</h1>
              <StatusBadge kind="session" value={s.state} />
              {recording ? (stream.connected ? <LiveIndicator /> : <span className="text-sm text-fg-muted">Connecting to live feed…</span>) : null}
            </div>
            <div className="flex flex-wrap items-center gap-2 text-sm text-fg-muted">
              <span>{serverNames.get(s.serverId) ?? "Server"}</span>
              <span aria-hidden>·</span>
              {s.folders.map((f) => (
                <Badge key={f.localPath} tone="neutral" className="font-mono">{f.localPath}</Badge>
              ))}
            </div>
            <div className="flex flex-wrap gap-1.5">
              {s.operations.map((op) => <StatusBadge key={op} kind="operation" value={op} />)}
              {s.includeReads ? <Badge tone="info">Content views</Badge> : null}
            </div>
          </div>

          <div className="flex items-center gap-2">
            <Button variant="secondary" onClick={exportCsv} disabled={exporting}>
              <Download aria-hidden />{exporting ? "Exporting…" : "Export CSV"}
            </Button>
            {STOPPABLE.includes(s.state) ? (
              <Button variant="danger" onClick={() => setConfirmStop(true)} disabled={stop.isPending}>
                <Square aria-hidden />Stop
              </Button>
            ) : null}
          </div>
        </div>

        {s.failureReason ? (
          <p className="flex items-center gap-2 rounded-md bg-danger-soft px-3 py-2 text-sm text-danger" role="alert">
            <AlertTriangle className="size-4 shrink-0" aria-hidden />
            {s.failureReason}
          </p>
        ) : null}
      </div>

      {integrity.data && integrity.data.length > 0 ? (
        <div className="rounded-md border border-warning/40 bg-warning-soft px-4 py-2" role="status">
          <p className="flex items-center gap-2 text-sm font-medium text-warning">
            <AlertTriangle className="size-4 shrink-0" aria-hidden />
            This record may be incomplete for part of the session
          </p>
          <IntegrityEvents events={integrity.data} showServer={false} />
        </div>
      ) : null}

      <div className="flex flex-wrap items-center gap-3">
        <Select value={operation} onChange={(e) => setOperation(e.target.value)} className="w-44" aria-label="Operation">
          <option value="">All operations</option>
          {s.operations.map((op) => <option key={op} value={op}>{op}</option>)}
        </Select>
        <Select value={access} onChange={(e) => setAccess(e.target.value)} className="w-40" aria-label="Access result">
          <option value="">Allowed and denied</option>
          <option value="allowed">Allowed</option>
          <option value="denied">Denied</option>
        </Select>
        <Input value={actorInput} onChange={(e) => setActorInput(e.target.value)} placeholder="Filter by user…" className="w-56" aria-label="User" />
        <span className="ml-auto text-sm tabular-nums text-fg-muted">{stream.rows.length.toLocaleString()} shown</span>
      </div>

      {stream.isError ? (
        <p className="text-sm text-danger" role="alert">{stream.error?.message}</p>
      ) : (
        <ActionsTable
          rows={stream.rows}
          fresh={stream.fresh}
          serverNames={serverNames}
          loading={stream.isLoading}
          hasMore={stream.hasMore}
          loadingMore={stream.loadingMore}
          onLoadOlder={stream.loadOlder}
          filtered={filtered}
        />
      )}

      <Dialog open={confirmStop} onOpenChange={setConfirmStop}>
        <DialogContent title="Stop this session?" description="The agent stops watching these folders. Recorded activity stays available.">
          <div className="flex justify-end gap-2">
            <DialogClose asChild><Button variant="secondary">Keep recording</Button></DialogClose>
            <Button
              variant="danger"
              disabled={stop.isPending}
              onClick={() => stop.mutate(id, { onSuccess: () => setConfirmStop(false) })}
            >
              Stop session
            </Button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}
