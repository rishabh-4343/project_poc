"use client";

import { Activity, Plug, Radio } from "lucide-react";
import { Area, AreaChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import { Badge } from "@/shared/ui/badge";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/shared/ui/card";
import { EmptyState } from "@/shared/ui/empty-state";
import { MetricCard } from "@/shared/ui/metric-card";
import { PageHeader } from "@/shared/ui/page-header";
import { Reveal } from "@/shared/ui/reveal";
import { Skeleton } from "@/shared/ui/skeleton";
import { StatusBadge } from "@/shared/ui/status-badge";
import { IntegrityEvents } from "../IntegrityEvents";
import { useAgents, useDashboard, useIntegrityEvents, useServers, useSessionUpdates } from "../hooks";
import { operationName } from "../operations";
import type { Dashboard } from "../types";

function utcDay(offset: number) {
  return new Date(Date.now() + offset * 86_400_000).toISOString().slice(0, 10);
}

// The API only returns days that had events; the chart and sparklines need every day, quiet ones as zero.
function fillDays(perDay: Dashboard["perDay"], days: number) {
  const byDay = new Map(perDay.map((p) => [p.day, p]));
  return Array.from({ length: days }, (_, i) => {
    const day = utcDay(i - days + 1);
    return byDay.get(day) ?? { day, actions: 0, denied: 0 };
  });
}

const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? "" : "s"}`;

export function DashboardPage() {
  const dashboard = useDashboard(14);
  const agents = useAgents();
  const integrity = useIntegrityEvents(8);
  const servers = useServers();
  useSessionUpdates();

  const d = dashboard.data;
  const series = d ? fillDays(d.perDay, d.days) : undefined;
  const yesterday = d?.perDay.find((p) => p.day === utcDay(-1));
  const online = agents.data?.filter((a) => a.online).length;
  const maxActor = Math.max(1, ...(d?.topActors.map((a) => a.actions) ?? [1]));

  return (
    <div className="flex flex-col gap-8">
      <Reveal>
        <PageHeader title="Dashboard" description="File server activity across all audited servers." />
      </Reveal>

      <Reveal index={1} className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-4">
        <MetricCard
          label="Events today"
          value={d?.eventsToday}
          previous={yesterday?.actions ?? (d ? 0 : null)}
          spark={series?.map((p) => p.actions)}
        />
        <MetricCard label="Active audits" value={d?.activeSessions} />
        <MetricCard label="Agents online" value={online} />
        <MetricCard
          label="Denied events"
          value={d?.deniedToday}
          previous={yesterday?.denied ?? (d ? 0 : null)}
          spark={series?.map((p) => p.denied)}
          tone="danger"
        />
      </Reveal>

      <Reveal index={2}>
        <Card>
          <CardHeader>
            <div className="flex flex-wrap items-start justify-between gap-3">
              <div className="flex flex-col gap-1">
                <CardTitle>Events over time</CardTitle>
                <CardDescription>Last 14 days, UTC.</CardDescription>
              </div>
              <ul className="flex items-center gap-4 text-sm text-fg-secondary" aria-label="Legend">
                <li className="flex items-center gap-2"><span className="h-0.5 w-4 rounded-full bg-(--chart-1)" aria-hidden />All events</li>
                <li className="flex items-center gap-2"><span className="h-0.5 w-4 rounded-full bg-(--chart-2)" aria-hidden />Denied</li>
              </ul>
            </div>
          </CardHeader>
          <CardContent>
            {!d ? (
              <Skeleton className="h-64 w-full" />
            ) : d.perDay.length === 0 ? (
              <EmptyState icon={Activity} title="No events yet" description="Events appear here as soon as an audit records file activity." />
            ) : (
              <div className="h-64">
                <ResponsiveContainer width="100%" height="100%">
                  <AreaChart data={series} margin={{ left: 0, right: 8, top: 8, bottom: 0 }}>
                    <CartesianGrid stroke="var(--border)" vertical={false} />
                    <XAxis dataKey="day" tickLine={false} axisLine={false} tick={{ fill: "var(--fg-muted)", fontSize: 12 }} />
                    <YAxis tickLine={false} axisLine={false} width={40} allowDecimals={false} tick={{ fill: "var(--fg-muted)", fontSize: 12 }} />
                    <Tooltip
                      contentStyle={{ background: "var(--surface)", border: "1px solid var(--border)", borderRadius: 8, color: "var(--fg)" }}
                      labelStyle={{ color: "var(--fg-secondary)" }}
                      itemStyle={{ color: "var(--fg)" }}
                      cursor={{ stroke: "var(--border)" }}
                    />
                    {/* Series colors are validated as a pair for both themes (globals.css --chart-1 / --chart-2). */}
                    <Area type="monotone" dataKey="actions" name="All events" stroke="var(--chart-1)" fill="var(--chart-1)" fillOpacity={0.1} strokeWidth={2} />
                    <Area type="monotone" dataKey="denied" name="Denied" stroke="var(--chart-2)" fill="var(--chart-2)" fillOpacity={0.1} strokeWidth={2} />
                  </AreaChart>
                </ResponsiveContainer>
              </div>
            )}
          </CardContent>
        </Card>
      </Reveal>

      <Reveal index={3} className="grid grid-cols-1 gap-4 lg:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle>Most active users</CardTitle>
            <CardDescription>By number of events, last 14 days.</CardDescription>
          </CardHeader>
          <CardContent className="flex flex-col gap-3">
            {!d ? (
              <Skeleton className="h-24 w-full" />
            ) : d.topActors.length === 0 ? (
              <p className="text-sm text-fg-muted">No activity yet.</p>
            ) : (
              d.topActors.map((a) => (
                <div key={a.actorSid} className="flex flex-col gap-1">
                  <div className="flex items-center justify-between gap-3 text-sm">
                    <span className="truncate font-mono text-xs" title={a.actorSid}>{a.actorSid}</span>
                    <span className="tabular-nums text-fg-muted">{a.actions.toLocaleString()}</span>
                  </div>
                  <div className="h-1.5 overflow-hidden rounded-full bg-surface-2">
                    <div className="h-full rounded-full bg-accent" style={{ width: `${(a.actions / maxActor) * 100}%` }} />
                  </div>
                </div>
              ))
            )}
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>Events by type</CardTitle>
            <CardDescription>Last 14 days.</CardDescription>
          </CardHeader>
          <CardContent className="flex flex-wrap gap-2">
            {!d ? (
              <Skeleton className="h-8 w-full" />
            ) : d.byOperation.length === 0 ? (
              <p className="text-sm text-fg-muted">No activity yet.</p>
            ) : (
              d.byOperation.map((o) => (
                <span key={o.operation} className="flex items-center gap-2">
                  <StatusBadge kind="operation" value={o.operation} label={operationName(o.operation)} />
                  <span className="text-sm tabular-nums text-fg-muted">{o.actions.toLocaleString()}</span>
                </span>
              ))
            )}
          </CardContent>
        </Card>
      </Reveal>

      <Reveal index={4}>
        <Card>
          <CardHeader>
            <CardTitle>Recording gaps</CardTitle>
            <CardDescription>Times when some activity may not have been recorded, for example while an agent was offline.</CardDescription>
          </CardHeader>
          <CardContent>
            {!integrity.data ? (
              <Skeleton className="h-16 w-full" />
            ) : integrity.data.length === 0 ? (
              <p className="text-sm text-fg-muted">None. Nothing has been missed.</p>
            ) : (
              <IntegrityEvents events={integrity.data} serverNames={new Map(servers.data?.map((s) => [s.id, s.hostName]))} />
            )}
          </CardContent>
        </Card>
      </Reveal>

      <Reveal index={5} className="flex flex-wrap gap-3 text-sm text-fg-muted">
        <Badge tone="neutral"><Plug className="size-3.5" aria-hidden />{plural(agents.data?.length ?? 0, "agent")} registered</Badge>
        <Badge tone="neutral"><Radio className="size-3.5" aria-hidden />{plural(d?.activeSessions ?? 0, "audit")} recording</Badge>
      </Reveal>
    </div>
  );
}
