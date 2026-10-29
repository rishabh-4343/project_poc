"use client";

import type { ReactNode } from "react";
import { AnimatePresence, motion, useReducedMotion } from "motion/react";
import { Folder, FileText, Radio, Share2 } from "lucide-react";
import { cn } from "@/shared/cn";
import { formatClock, formatDay } from "@/shared/format";
import { Badge } from "@/shared/ui/badge";
import { Button } from "@/shared/ui/button";
import { EmptyState } from "@/shared/ui/empty-state";
import { Skeleton } from "@/shared/ui/skeleton";
import { StatusBadge } from "@/shared/ui/status-badge";
import { eventLabel } from "./operations";
import type { ActionRow } from "./types";

// Column widths are fixed (only "File or folder" stretches) and sized to fit a 1440px laptop with the sidebar open.
// Header and rows share one template, so they always line up. Tailwind needs both templates as literal strings.
const GRID = "grid grid-cols-[76px_88px_140px_minmax(200px,1fr)_200px_80px_104px_104px] items-center gap-x-3";
const GRID_CLASSIFIED = "grid grid-cols-[76px_88px_140px_minmax(200px,1fr)_200px_80px_104px_104px_160px] items-center gap-x-3";
const HEADERS = ["Time", "Server", "User", "File or folder", "Event", "Result", "Process", "Client IP"];
const ANIMATED_ROWS = 40;

const objectIcon: Record<string, typeof FileText> = { File: FileText, Folder, Share: Share2 };

const baseName = (p: string) => p.split("\\").filter(Boolean).pop() ?? p;

function riskTone(level: number) {
  return level >= 3 ? "danger" : level === 2 ? "warning" : level === 1 ? "info" : "neutral";
}

const isClassified = (r: ActionRow) => Boolean(r.contentType) || r.riskLevel >= 0;

function Row({ row, index, server, fresh, grid, classified }: {
  row: ActionRow; index: number; server: string; fresh: boolean; grid: string; classified: boolean;
}) {
  const reduced = useReducedMotion();
  const Icon = objectIcon[row.objectType] ?? FileText;
  const renamed = row.newPath && row.newPath !== row.path;
  const fullPath = renamed ? `${row.path} → ${row.newPath}` : row.path;

  return (
    <motion.div
      role="row"
      layout={index < ANIMATED_ROWS ? "position" : false}
      initial={fresh ? { opacity: 0, y: reduced ? 0 : -8 } : false}
      animate={{ opacity: 1, y: 0 }}
      exit={{ opacity: 0 }}
      transition={{ duration: 0.25, ease: "easeOut" }}
      className={cn(grid, "border-b border-border px-4 py-2.5 text-sm last:border-0 hover:bg-surface-2", fresh && "row-flash")}
    >
      <span role="cell" className="flex flex-col leading-tight tabular-nums">
        <span>{formatClock(row.occurredAt)}</span>
        <span className="text-xs text-fg-muted">{formatDay(row.occurredAt)}</span>
      </span>
      <span role="cell" className="truncate text-fg-secondary" title={server}>{server}</span>
      <span role="cell" className="truncate" title={`${row.actorName ?? ""} ${row.actorSid}`.trim()}>{row.actorName || row.actorSid}</span>
      <span role="cell" className="flex min-w-0 items-start gap-2">
        <Icon className="mt-0.5 size-4 shrink-0 text-fg-muted" aria-hidden />
        <span className="flex min-w-0 flex-col leading-tight">
          <span className="truncate font-medium" title={baseName(renamed ? row.newPath! : row.path)}>{baseName(renamed ? row.newPath! : row.path)}</span>
          <span className="truncate font-mono text-xs text-fg-muted" title={fullPath}>{fullPath}</span>
        </span>
      </span>
      <span role="cell" className="flex flex-wrap items-center gap-1.5">
        <StatusBadge kind="operation" value={row.operation} label={eventLabel(row.operation, row.objectType, row.success)} className="whitespace-normal" />
        {row.certainty === "Inferred" ? <Badge tone="neutral" title="Worked out by the agent rather than reported directly">Inferred</Badge> : null}
      </span>
      <span role="cell"><StatusBadge kind="access" value={row.success} /></span>
      <span role="cell" className="truncate text-fg-secondary" title={row.processName ?? ""}>{row.processName ?? "—"}</span>
      <span role="cell" className="truncate font-mono text-xs text-fg-secondary">{row.clientIp ?? "—"}</span>
      {classified ? (
        <span role="cell" className="flex min-w-0 items-center gap-1.5">
          {row.contentType ? <Badge tone="neutral" className="truncate">{row.contentType}</Badge> : null}
          {row.riskLevel >= 0 ? <Badge tone={riskTone(row.riskLevel)}>Risk {row.riskLevel}</Badge> : null}
          {!isClassified(row) ? <span className="text-fg-muted">—</span> : null}
        </span>
      ) : null}
    </motion.div>
  );
}

type Props = {
  rows: ActionRow[];
  fresh: Set<string>;
  serverNames: Map<string, string>;
  loading: boolean;
  hasMore: boolean;
  loadingMore: boolean;
  onLoadOlder: () => void;
  filtered: boolean;
  // Replaces the default "waiting for activity" message when there are no rows.
  empty?: ReactNode;
};

// New rows enter at the top with a fade, rise and one-time accent flash; existing rows slide down.
// The card scrolls on both axes, so the header sticks to the card's top and never covers a row.
export function ActionsTable({ rows, fresh, serverNames, loading, hasMore, loadingMore, onLoadOlder, filtered, empty }: Props) {
  const classified = rows.some(isClassified);
  const grid = classified ? GRID_CLASSIFIED : GRID;
  const headers = classified ? [...HEADERS, "Classification"] : HEADERS;

  return (
    <div className="max-h-[calc(100vh-14rem)] overflow-auto rounded-lg border border-border bg-surface shadow-card">
      <div role="table" aria-label="Recorded events" className={classified ? "min-w-[1280px]" : "min-w-[1108px]"}>
        <div role="row" className={cn(grid, "sticky top-0 z-[1] border-b border-border bg-surface-2 px-4 py-2.5 text-xs font-semibold uppercase tracking-wide text-fg-secondary")}>
          {headers.map((h) => <span key={h} role="columnheader">{h}</span>)}
        </div>

        {loading ? (
          Array.from({ length: 6 }, (_, i) => (
            <div key={i} className={cn(grid, "border-b border-border px-4 py-3")}>
              {headers.map((h) => <Skeleton key={h} className="h-4 w-full" />)}
            </div>
          ))
        ) : rows.length === 0 ? (
          <div className="p-6">
            {empty ?? (
              <EmptyState
                icon={Radio}
                title={filtered ? "Nothing matches these filters" : "Waiting for activity"}
                description={filtered ? "Clear or change the filters to see more." : "Create, change, rename or delete a file in a watched folder and it appears here within a few seconds."}
              />
            )}
          </div>
        ) : (
          <AnimatePresence initial={false}>
            {rows.map((row, i) => (
              <Row
                key={`${row.serverId}:${row.seq}`}
                row={row}
                index={i}
                server={serverNames.get(row.serverId) ?? "—"}
                fresh={fresh.has(`${row.serverId}:${row.seq}`)}
                grid={grid}
                classified={classified}
              />
            ))}
          </AnimatePresence>
        )}
      </div>

      {hasMore ? (
        <div className="flex justify-center border-t border-border p-3">
          <Button variant="secondary" size="sm" onClick={onLoadOlder} disabled={loadingMore}>
            {loadingMore ? "Loading…" : "Load older events"}
          </Button>
        </div>
      ) : null}
    </div>
  );
}
