"use client";

import type { ReactNode } from "react";
import { Eye, Hourglass } from "lucide-react";
import type { ColumnDef } from "@tanstack/react-table";
import { formatDateTime } from "@/shared/format";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/shared/ui/card";
import { DataTable } from "@/shared/ui/data-table";
import { EmptyState } from "@/shared/ui/empty-state";
import { StatusBadge } from "@/shared/ui/status-badge";
import { useContentViews } from "./hooks";
import { eventLabel } from "./operations";
import type { ContentView } from "./types";

// Shown wherever an event kind has never been recorded on any server, instead of an empty table.
export function NotCapturedState({ what }: { what: string }) {
  return (
    <EmptyState
      icon={Hourglass}
      title="Not being captured yet"
      description={`No ${what} have been recorded on any server so far. They appear here as soon as the file server driver reports one.`}
    />
  );
}

function columns(serverNames?: Map<string, string>): ColumnDef<ContentView>[] {
  return [
    { header: "Last read", cell: ({ row }) => <span className="whitespace-nowrap tabular-nums text-fg-secondary">{formatDateTime(row.original.lastAt)}</span> },
    ...(serverNames
      ? [{ header: "Server", cell: ({ row }) => <span className="text-fg-secondary">{serverNames.get(row.original.serverId) ?? "—"}</span> } satisfies ColumnDef<ContentView>]
      : []),
    { header: "User", cell: ({ row }) => <span title={row.original.actorSid}>{row.original.actorName || row.original.actorSid}</span> },
    {
      header: "File or folder",
      cell: ({ row }) => (
        <span className="block max-w-md truncate font-mono text-xs text-fg-secondary" title={row.original.path}>{row.original.path}</span>
      ),
    },
    {
      header: "Event",
      cell: ({ row }) => (
        // The agent merges Read and Content View into one row, so the row carries the umbrella "Read".
        <StatusBadge kind="operation" value="Read" label={eventLabel("Read", row.original.objectType, row.original.success)} />
      ),
    },
    { header: "Reads", cell: ({ row }) => <span className="tabular-nums">{row.original.readCount.toLocaleString()}</span> },
    { header: "Process", cell: ({ row }) => <span className="text-fg-secondary">{row.original.processName ?? "—"}</span> },
    { header: "Client IP", cell: ({ row }) => <span className="font-mono text-xs text-fg-secondary">{row.original.clientIp ?? "—"}</span> },
  ];
}

// One row per user, file and hour, with how many times it was read in that hour.
export function ContentViewsTable({ rows, loading, serverNames, empty }: {
  rows: ContentView[]; loading: boolean; serverNames?: Map<string, string>; empty: ReactNode;
}) {
  return (
    <DataTable
      columns={columns(serverNames)}
      data={rows}
      getRowId={(v) => `${v.serverId}:${v.bucketStart}:${v.actorSid}:${v.path}:${v.success}`}
      loading={loading}
      empty={empty}
    />
  );
}

// File reads for audits that asked for them.
export function ContentViews({ sessionId, recording }: { sessionId: string; recording: boolean }) {
  const views = useContentViews(sessionId, recording);

  return (
    <Card>
      <CardHeader>
        <CardTitle>File reads</CardTitle>
        <CardDescription>Who opened and read files in this audit, grouped to one row per user, file and hour.</CardDescription>
      </CardHeader>
      <CardContent>
        {views.isError ? (
          <p className="text-sm text-danger" role="alert">{views.error.message}</p>
        ) : (
          <ContentViewsTable
            rows={views.data?.items ?? []}
            loading={views.isPending}
            empty={
              views.data && !views.data.captured ? (
                <NotCapturedState what="file reads" />
              ) : (
                <EmptyState icon={Eye} title="No file reads yet" description="Open a file in a watched folder and the read appears here within a minute." />
              )
            }
          />
        )}
      </CardContent>
    </Card>
  );
}
