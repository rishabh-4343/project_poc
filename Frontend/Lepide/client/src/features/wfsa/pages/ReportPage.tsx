"use client";

import { useMemo, useState } from "react";
import Link from "next/link";
import { ArrowLeft, FileQuestion, SearchX } from "lucide-react";
import { Button } from "@/shared/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/shared/ui/card";
import { EmptyState } from "@/shared/ui/empty-state";
import { Select } from "@/shared/ui/input";
import { PageHeader } from "@/shared/ui/page-header";
import { Reveal } from "@/shared/ui/reveal";
import { Skeleton } from "@/shared/ui/skeleton";
import { ActionsTable } from "../ActionsTable";
import { ContentViewsTable, NotCapturedState } from "../ContentViews";
import { useReportActions, useReportReads, useServers } from "../hooks";
import { findReport } from "../reports";

const PERIODS = [
  { days: 1, label: "Last 24 hours" },
  { days: 7, label: "Last 7 days" },
  { days: 30, label: "Last 30 days" },
  { days: 90, label: "Last 90 days" },
];

const NO_FRESH = new Set<string>();

export function ReportPageSkeleton() {
  return (
    <div className="flex flex-col gap-6">
      <Skeleton className="h-8 w-80" />
      <Skeleton className="h-5 w-96" />
      <Skeleton className="h-96 w-full" />
    </div>
  );
}

export function ReportPage({ slug }: { slug: string }) {
  const spec = findReport(slug);
  const [days, setDays] = useState(7);
  const servers = useServers();
  const actions = useReportActions(slug, spec?.actions, days);
  const reads = useReportReads(slug, spec?.reads, days);
  const serverNames = useMemo(() => new Map(servers.data?.map((s) => [s.id, s.hostName])), [servers.data]);

  const back = (
    <Button variant="ghost" size="sm" asChild className="-ml-3 self-start">
      <Link href="/wfsa/reports"><ArrowLeft aria-hidden />All reports</Link>
    </Button>
  );

  if (!spec) {
    return (
      <div className="flex flex-col gap-4">
        {back}
        <EmptyState icon={FileQuestion} title="Report not found" description="This report does not exist. Pick one from the list of reports." />
      </div>
    );
  }

  const actionRows = actions.data?.pages.flatMap((p) => p.items) ?? [];
  const readRows = reads.data?.items ?? [];
  const loading = (spec.actions !== undefined && actions.isPending) || (spec.reads !== undefined && reads.isPending);
  const error = actions.error ?? reads.error;
  const captured = Boolean(actions.data?.pages[0]?.captured) || Boolean(reads.data?.captured);
  const total = actionRows.length + readRows.length;
  const period = PERIODS.find((p) => p.days === days)!.label.toLowerCase();

  return (
    <div className="flex flex-col gap-5">
      <div className="flex flex-col gap-3">
        {back}
        <Reveal>
          <PageHeader
            title={spec.title}
            description={spec.description}
            actions={
              <Select value={days} onChange={(e) => setDays(Number(e.target.value))} aria-label="Period" className="w-44">
                {PERIODS.map((p) => <option key={p.days} value={p.days}>{p.label}</option>)}
              </Select>
            }
          />
        </Reveal>
      </div>

      {error ? (
        <p className="text-sm text-danger" role="alert">Could not load this report: {error.message}</p>
      ) : loading ? (
        spec.actions ? (
          <ActionsTable rows={[]} fresh={NO_FRESH} serverNames={serverNames} loading hasMore={false} loadingMore={false} onLoadOlder={() => {}} filtered={false} />
        ) : (
          <ContentViewsTable rows={[]} loading serverNames={serverNames} empty={null} />
        )
      ) : total === 0 ? (
        captured ? (
          <EmptyState icon={SearchX} title={`No events in the ${period}`} description="Nothing recorded on any server matches this report in that period. Choose a longer period to look further back." />
        ) : (
          <NotCapturedState what="events of this kind" />
        )
      ) : (
        <Reveal index={1} className="flex flex-col gap-5">
          <p className="text-sm text-fg-secondary tabular-nums">
            {total.toLocaleString()} {total === 1 ? "event" : "events"} in the {period}
            {actions.hasNextPage ? ", newest first. Load older events at the bottom." : "."}
          </p>

          {readRows.length > 0 ? (
            <Card>
              <CardHeader>
                <CardTitle>File reads</CardTitle>
                <CardDescription>Grouped to one row per user, file and hour, with how many times it was read.</CardDescription>
              </CardHeader>
              <CardContent>
                <ContentViewsTable rows={readRows} loading={false} serverNames={serverNames} empty={null} />
              </CardContent>
            </Card>
          ) : null}

          {actionRows.length > 0 ? (
            <ActionsTable
              rows={actionRows}
              fresh={NO_FRESH}
              serverNames={serverNames}
              loading={false}
              hasMore={actions.hasNextPage}
              loadingMore={actions.isFetchingNextPage}
              onLoadOlder={() => actions.fetchNextPage()}
              filtered={false}
            />
          ) : null}
        </Reveal>
      )}
    </div>
  );
}
