"use client";

import { useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { Plus, Radio } from "lucide-react";
import type { ColumnDef } from "@tanstack/react-table";
import { formatDateTime } from "@/shared/format";
import { Button } from "@/shared/ui/button";
import { DataTable } from "@/shared/ui/data-table";
import { EmptyState } from "@/shared/ui/empty-state";
import { Select } from "@/shared/ui/input";
import { PageHeader } from "@/shared/ui/page-header";
import { Reveal } from "@/shared/ui/reveal";
import { StatusBadge } from "@/shared/ui/status-badge";
import { useServers, useSessions, useSessionUpdates } from "../hooks";
import type { Session, SessionState } from "../types";

const STATES: SessionState[] = ["Requested", "Starting", "Active", "Degraded", "Stopping", "Completed", "Failed"];

export function SessionsPage() {
  const router = useRouter();
  const sessions = useSessions();
  const servers = useServers();
  const [state, setState] = useState("");
  useSessionUpdates();

  const host = new Map(servers.data?.map((s) => [s.id, s.hostName]));

  const columns: ColumnDef<Session>[] = [
    { header: "Session", cell: ({ row }) => <span className="font-medium">{row.original.name || "Untitled session"}</span> },
    { header: "Server", cell: ({ row }) => host.get(row.original.serverId) ?? "—" },
    { header: "State", cell: ({ row }) => <StatusBadge kind="session" value={row.original.state} /> },
    {
      header: "Scope",
      cell: ({ row }) => (
        <span className="font-mono text-xs text-fg-muted" title={row.original.folders.map((f) => f.localPath).join("\n")}>
          {row.original.folders[0]?.localPath}
          {row.original.folders.length > 1 ? ` +${row.original.folders.length - 1}` : ""}
        </span>
      ),
    },
    { header: "Operations", cell: ({ row }) => <span className="tabular-nums text-fg-muted">{row.original.operations.length}</span> },
    { header: "Requested", cell: ({ row }) => <span className="text-fg-muted">{formatDateTime(row.original.requestedAt)}</span> },
  ];

  const rows = (sessions.data ?? []).filter((s) => !state || s.state === state);

  return (
    <div className="flex flex-col gap-6">
      <Reveal>
        <PageHeader
          title="Sessions"
          description="Each session watches folders on one server and records what happens in them."
          actions={<Button asChild><Link href="/wfsa/sessions/new"><Plus aria-hidden />New session</Link></Button>}
        />
      </Reveal>
      <Reveal index={1} className="flex flex-col gap-4">
        <div className="max-w-xs">
          <Select value={state} onChange={(e) => setState(e.target.value)} aria-label="Filter by state">
            <option value="">All states</option>
            {STATES.map((s) => <option key={s} value={s}>{s}</option>)}
          </Select>
        </div>
        {sessions.isError ? (
          <p className="text-sm text-danger">{sessions.error.message}</p>
        ) : (
          <DataTable
            columns={columns}
            data={rows}
            getRowId={(s) => s.id}
            loading={sessions.isPending}
            onRowClick={(s) => router.push(`/wfsa/sessions/${s.id}`)}
            empty={
              <EmptyState
                icon={Radio}
                title={state ? `No ${state.toLowerCase()} sessions` : "No sessions yet"}
                description="Start a session to record file activity on a server and watch it live."
                action={<Button asChild><Link href="/wfsa/sessions/new"><Plus aria-hidden />New session</Link></Button>}
              />
            }
          />
        )}
      </Reveal>
    </div>
  );
}
