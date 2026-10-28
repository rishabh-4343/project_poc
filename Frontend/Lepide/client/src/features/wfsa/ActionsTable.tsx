"use client";

import { AnimatePresence, motion, useReducedMotion } from "motion/react";
import { Folder, FileText, Radio, Share2 } from "lucide-react";
import { cn } from "@/shared/cn";
import { formatClock } from "@/shared/format";
import { Badge } from "@/shared/ui/badge";
import { Button } from "@/shared/ui/button";
import { EmptyState } from "@/shared/ui/empty-state";
import { Skeleton } from "@/shared/ui/skeleton";
import { StatusBadge } from "@/shared/ui/status-badge";
import type { ActionRow } from "./types";

const GRID = "grid grid-cols-[84px_90px_150px_150px_minmax(220px,1fr)_170px_90px_120px_110px_170px] items-center gap-x-4";
const HEADERS = ["When", "Server", "Who", "Object", "Path", "Operation", "Status", "Process", "From", "Classification"];
const ANIMATED_ROWS = 40;

const objectIcon: Record<string, typeof FileText> = { File: FileText, Folder, Share: Share2 };

const baseName = (p: string) => p.split("\\").filter(Boolean).pop() ?? p;

function riskTone(level: number) {
  return level >= 3 ? "danger" : level === 2 ? "warning" : level === 1 ? "info" : "neutral";
}

function Row({ row, index, server, fresh }: { row: ActionRow; index: number; server: string; fresh: boolean }) {
  const reduced = useReducedMotion();
  const Icon = objectIcon[row.objectType] ?? FileText;
  const renamed = row.newPath && row.newPath !== row.path;

  return (
    <motion.div
      role="row"
      layout={index < ANIMATED_ROWS ? "position" : false}
      initial={fresh ? { opacity: 0, y: reduced ? 0 : -8 } : false}
      animate={{ opacity: 1, y: 0 }}
      exit={{ opacity: 0 }}
      transition={{ duration: 0.25, ease: "easeOut" }}
      className={cn(GRID, "border-b border-border px-4 py-2.5 text-sm last:border-0", fresh && "row-flash")}
    >
      <span role="cell" className="tabular-nums text-fg-muted">{formatClock(row.occurredAt)}</span>
      <span role="cell" className="truncate text-fg-muted" title={server}>{server}</span>
      <span role="cell" className="truncate" title={`${row.actorName ?? ""} ${row.actorSid}`.trim()}>{row.actorName || row.actorSid}</span>
      <span role="cell" className="flex min-w-0 items-center gap-2">
        <Icon className="size-4 shrink-0 text-fg-muted" aria-hidden />
        <span className="truncate">{baseName(renamed ? row.newPath! : row.path)}</span>
      </span>
      <span role="cell" className="truncate font-mono text-xs text-fg-muted" title={renamed ? `${row.path} → ${row.newPath}` : row.path}>
        {renamed ? `${row.path} → ${row.newPath}` : row.path}
      </span>
      <span role="cell" className="flex items-center gap-1.5">
        <StatusBadge kind="operation" value={row.operation} />
        {row.certainty === "Inferred" ? <Badge tone="neutral" title="Derived by the agent, not seen directly">Inferred</Badge> : null}
      </span>
      <span role="cell"><StatusBadge kind="access" value={row.success} /></span>
      <span role="cell" className="truncate text-fg-muted" title={row.processName ?? ""}>{row.processName ?? "—"}</span>
      <span role="cell" className="truncate font-mono text-xs text-fg-muted">{row.clientIp ?? "—"}</span>
      <span role="cell" className="flex min-w-0 items-center gap-1.5">
        {row.contentType ? <Badge tone="neutral" className="truncate">{row.contentType}</Badge> : null}
        {row.riskLevel >= 0 ? <Badge tone={riskTone(row.riskLevel)}>Risk {row.riskLevel}</Badge> : null}
        {!row.contentType && row.riskLevel < 0 ? <span className="text-fg-muted">—</span> : null}
      </span>
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
};

// New rows enter at the top with a fade, rise and one-time accent flash; existing rows slide down.
export function ActionsTable({ rows, fresh, serverNames, loading, hasMore, loadingMore, onLoadOlder, filtered }: Props) {
  return (
    <div className="overflow-x-auto rounded-lg border border-border bg-surface shadow-card">
      <div role="table" aria-label="Recorded actions" className="min-w-[1360px]">
        <div role="row" className={cn(GRID, "sticky top-14 z-[1] border-b border-border bg-surface-2 px-4 py-2.5 text-xs font-medium uppercase tracking-wide text-fg-muted")}>
          {HEADERS.map((h) => <span key={h} role="columnheader">{h}</span>)}
        </div>

        {loading ? (
          Array.from({ length: 6 }, (_, i) => (
            <div key={i} className={cn(GRID, "border-b border-border px-4 py-3")}>
              {HEADERS.map((h) => <Skeleton key={h} className="h-4 w-full" />)}
            </div>
          ))
        ) : rows.length === 0 ? (
          <div className="p-6">
            <EmptyState
              icon={Radio}
              title={filtered ? "Nothing matches these filters" : "Waiting for activity"}
              description={filtered ? "Clear or change the filters to see more." : "Create, modify, delete or rename a file in a watched folder and it appears here."}
            />
          </div>
        ) : (
          <AnimatePresence initial={false}>
            {rows.map((row, i) => (
              <Row key={`${row.serverId}:${row.seq}`} row={row} index={i} server={serverNames.get(row.serverId) ?? "—"} fresh={fresh.has(`${row.serverId}:${row.seq}`)} />
            ))}
          </AnimatePresence>
        )}
      </div>

      {hasMore ? (
        <div className="flex justify-center border-t border-border p-3">
          <Button variant="secondary" size="sm" onClick={onLoadOlder} disabled={loadingMore}>
            {loadingMore ? "Loading…" : "Load older"}
          </Button>
        </div>
      ) : null}
    </div>
  );
}
