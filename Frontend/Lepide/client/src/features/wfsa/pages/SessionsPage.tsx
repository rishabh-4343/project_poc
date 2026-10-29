"use client";

import { useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { FileBarChart, Plus, Radio } from "lucide-react";
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
const STATE_LABELS: Partial<Record<string, string>> = { Requested: "Pending", Degraded: "Needs attention" };

export function SessionsPage() {
  const router = useRouter();
  const sessions = useSessions();
  const servers = useServers();
  const [state, setState] = useState("");
  useSessionUpdates();

  const host = new Map(servers.data?.map((s) => [s.id, s.hostName]));

  const columns: ColumnDef<Session>[] = [
    { header: "Audit", cell: ({ row }) => <span className="font-medium">{row.original.name || "Untitled audit"}</span> },
    { header: "Server", cell: ({ row }) => host.get(row.original.serverId) ?? "—" },
    { header: "Status", cell: ({ row }) => <StatusBadge kind="session" value={row.original.state} /> },
    {
      header: "Folders",
      cell: ({ row }) => (
        <span className="font-mono text-xs text-fg-muted" title={row.original.folders.map((f) => f.localPath).join("\n")}>
          {row.original.folders[0]?.localPath}
          {row.original.folders.length > 1 ? ` +${row.original.folders.length - 1}` : ""}
        </span>
      ),
    },
    { header: "Event types", cell: ({ row }) => <span className="tabular-nums text-fg-secondary">{row.original.operations.length}</span> },
    { header: "Started", cell: ({ row }) => <span className="text-fg-secondary">{formatDateTime(row.original.requestedAt)}</span> },
    {
      header: "",
      id: "open",
      cell: ({ row }) => (
        <Button size="sm" variant="secondary" asChild>
          <Link href={`/wfsa/sessions/${row.original.id}`} onClick={(e) => e.stopPropagation()}>
            <FileBarChart aria-hidden />Open report
          </Link>
        </Button>
      ),
    },
  ];

  const rows = (sessions.data ?? []).filter((s) => !state || s.state === state);

  return (
    <div className="flex flex-col gap-6">
      <Reveal>
        <PageHeader
          title="Audits"
          description="An audit records who changed what in the folders you choose on one server. Open an audit's report to see everything it recorded, live while it runs."
          actions={<Button asChild><Link href="/wfsa/sessions/new"><Plus aria-hidden />New audit</Link></Button>}
        />
      </Reveal>
      <Reveal index={1} className="flex flex-col gap-4">
        <div className="max-w-xs">
          <Select value={state} onChange={(e) => setState(e.target.value)} aria-label="Filter by status">
            <option value="">All statuses</option>
            {STATES.map((s) => <option key={s} value={s}>{STATE_LABELS[s] ?? s}</option>)}
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
                title={state ? `No audits with status "${STATE_LABELS[state] ?? state}"` : "No audits yet"}
                description={state ? "Choose another status to see more audits." : "Start an audit: pick a server, the folders to watch and what to record. Its report fills in as activity happens."}
                action={state ? undefined : <Button asChild><Link href="/wfsa/sessions/new"><Plus aria-hidden />New audit</Link></Button>}
              />
            }
          />
        )}
      </Reveal>
    </div>
  );
}
