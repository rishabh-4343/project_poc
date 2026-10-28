import { ShieldAlert } from "lucide-react";
import { formatDateTime } from "@/shared/format";
import { Badge, type BadgeTone } from "@/shared/ui/badge";
import type { IntegrityEvent, IntegrityKind } from "./types";

const KINDS: Record<IntegrityKind, { label: string; tone: BadgeTone }> = {
  DriverOverflow: { label: "Driver overflow", tone: "danger" },
  Gap: { label: "Gap in coverage", tone: "danger" },
  GapRecovered: { label: "Gap recovered", tone: "success" },
  AgentOffline: { label: "Agent offline", tone: "warning" },
  DriverUnloaded: { label: "Driver unloaded", tone: "danger" },
  TamperSuspected: { label: "Tampering suspected", tone: "danger" },
};

// details arrives as jsonb text: { droppedCount, message }.
function describe(e: IntegrityEvent) {
  const parts: string[] = [];
  try {
    const d = e.details ? (JSON.parse(e.details) as { droppedCount?: number | null; message?: string | null }) : null;
    if (d?.droppedCount) parts.push(`${d.droppedCount.toLocaleString()} dropped`);
    if (d?.message) parts.push(d.message);
  } catch {
    // Not JSON: show nothing rather than raw text.
  }
  if (e.firstSeq !== null) {
    parts.push(e.lastSeq !== null ? `seq ${e.firstSeq}-${e.lastSeq}` : `from seq ${e.firstSeq}`);
  }
  return parts.join(" · ");
}

type Props = { events: IntegrityEvent[]; serverNames?: Map<string, string>; showServer?: boolean };

// Periods where the record may be incomplete. An open event (no end time) is still happening.
export function IntegrityEvents({ events, serverNames, showServer = true }: Props) {
  return (
    <ul className="flex flex-col divide-y divide-border">
      {events.map((e) => {
        const kind = KINDS[e.kind] ?? { label: e.kind, tone: "neutral" as BadgeTone };
        const detail = describe(e);
        return (
          <li key={e.id} className="flex flex-wrap items-center gap-x-3 gap-y-1 py-2 text-sm">
            <Badge tone={kind.tone}>
              <ShieldAlert className="size-3.5" aria-hidden />
              {kind.label}
            </Badge>
            {showServer ? <span className="font-medium">{serverNames?.get(e.serverId) ?? "Server"}</span> : null}
            <span className="text-fg-muted">
              {formatDateTime(e.startedAt)}
              {e.endedAt ? ` – ${formatDateTime(e.endedAt)}` : " – ongoing"}
            </span>
            {detail ? <span className="text-fg-muted">{detail}</span> : null}
          </li>
        );
      })}
    </ul>
  );
}
