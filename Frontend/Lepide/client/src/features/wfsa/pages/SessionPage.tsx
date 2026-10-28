"use client";

import { useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { AlertTriangle, ArrowLeft, Download, Square } from "lucide-react";
import { toast } from "sonner";
import { Badge } from "@/shared/ui/badge";
import { Button } from "@/shared/ui/button";
import { Dialog, DialogClose, DialogContent } from "@/shared/ui/dialog";
import { Input, Select } from "@/shared/ui/input";
import { LiveIndicator, PausedIndicator } from "@/shared/ui/live-indicator";
import { Skeleton } from "@/shared/ui/skeleton";
import { StatusBadge } from "@/shared/ui/status-badge";
import { ActionsTable } from "../ActionsTable";
import { ContentViews } from "../ContentViews";
import { operationName } from "../operations";
import { IntegrityEvents } from "../IntegrityEvents";
import { wfsaApi } from "../api";
import { useAgents, useServers, useSession, useSessionIntegrity, useStopSession } from "../hooks";
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
  const agents = useAgents();
  const stop = useStopSession();
  const integrity = useSessionIntegrity(id);

  const [operation, setOperation] = useState("");
  const [access, setAccess] = useState("");
  const [actorInput, setActorInput] = useState("");
  const [actor, setActor] = useState("");
  const [confirmStop, setConfirmStop] = useState(false);
  const [showAllGaps, setShowAllGaps] = useState(false);
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
  // Unknown while the agents list loads; only a confirmed offline agent pauses the header.
  const agentOnline = agents.data?.find((a) => a.id === s.agentId)?.online;

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
          <Link href="/wfsa/sessions"><ArrowLeft aria-hidden />Audits</Link>
        </Button>

        <div className="flex flex-wrap items-start justify-between gap-4">
          <div className="flex flex-col gap-2">
            <div className="flex flex-wrap items-center gap-3">
              <h1 className="text-[22px] font-semibold leading-tight tracking-tight">{s.name || "Untitled audit"}</h1>
              <StatusBadge kind="session" value={s.state} />
              {s.state === "Stopping" ? (
                <PausedIndicator label="Not recording" tone="muted" title="Waiting for the agent to confirm the stop." />
              ) : s.state === "Degraded" ? (
                <PausedIndicator label="Recording not confirmed" title="The agent reconnected but could not confirm it is still watching these folders." />
              ) : s.state !== "Active" ? null : agentOnline === false ? (
                <PausedIndicator label="Paused · agent offline" title="Nothing is recorded until the agent reconnects." />
              ) : stream.connected ? (
                <LiveIndicator />
              ) : (
                <span className="text-sm text-fg-muted">Connecting to live feed…</span>
              )}
            </div>
            <div className="flex flex-wrap items-center gap-2 text-sm text-fg-secondary">
              <span>{serverNames.get(s.serverId) ?? "Server"}</span>
              <span aria-hidden>·</span>
              {s.folders.map((f) => (
                <Badge key={f.localPath} tone="neutral" className="font-mono">{f.localPath}</Badge>
              ))}
            </div>
            <div className="flex flex-wrap gap-1.5">
              {s.operations.map((op) => <StatusBadge key={op} kind="operation" value={op} label={operationName(op)} />)}
              {s.includeReads ? <Badge tone="info">File reads</Badge> : null}
            </div>
            <p className="max-w-3xl text-sm text-fg-secondary">
              This is the audit&apos;s report: every recorded event, newest first. Filter it below or export it as CSV.{" "}
              For views across all servers, see <Link href="/wfsa/reports" className="font-medium text-accent-text underline-offset-2 hover:underline">Reports</Link>.
            </p>
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
            Some activity during this audit may not have been recorded
          </p>
          {/* The latest two gaps; the rest on request, so a long outage history does not push the report off screen. */}
          <IntegrityEvents events={showAllGaps ? integrity.data : integrity.data.slice(0, 2)} showServer={false} />
          {integrity.data.length > 2 ? (
            <button
              type="button"
              onClick={() => setShowAllGaps((v) => !v)}
              className="pb-1 text-sm font-medium text-warning underline-offset-2 hover:underline"
            >
              {showAllGaps ? "Show fewer" : `Show all ${integrity.data.length}`}
            </button>
          ) : null}
        </div>
      ) : null}

      <div className="flex flex-wrap items-center gap-3">
        <Select value={operation} onChange={(e) => setOperation(e.target.value)} className="w-64" aria-label="Event">
          <option value="">All events</option>
          {s.operations.map((op) => <option key={op} value={op}>{operationName(op)}</option>)}
        </Select>
        <Select value={access} onChange={(e) => setAccess(e.target.value)} className="w-48" aria-label="Result">
          <option value="">Allowed and denied</option>
          <option value="allowed">Allowed only</option>
          <option value="denied">Denied only</option>
        </Select>
        <Input value={actorInput} onChange={(e) => setActorInput(e.target.value)} placeholder="Filter by user…" className="w-56" aria-label="User" />
        <span className="ml-auto text-sm tabular-nums text-fg-secondary">{stream.rows.length.toLocaleString()} shown</span>
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

      {s.includeReads ? <ContentViews sessionId={id} recording={recording} /> : null}

      <Dialog open={confirmStop} onOpenChange={setConfirmStop}>
        <DialogContent title="Stop this audit?" description="These folders stop being watched. Everything recorded so far stays in the report.">
          <div className="flex justify-end gap-2">
            <DialogClose asChild><Button variant="secondary">Keep recording</Button></DialogClose>
            <Button
              variant="danger"
              disabled={stop.isPending}
              onClick={() => stop.mutate(id, { onSuccess: () => setConfirmStop(false) })}
            >
              Stop audit
            </Button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}
