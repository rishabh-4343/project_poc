"use client";

import Link from "next/link";
import { ChevronRight } from "lucide-react";
import { PageHeader } from "@/shared/ui/page-header";
import { Reveal } from "@/shared/ui/reveal";
import { REPORT_TREE, isGroup, reportHref, type ReportSpec } from "../reports";

function ReportCard({ report }: { report: ReportSpec }) {
  return (
    <Link
      href={reportHref(report.slug)}
      className="group flex items-start justify-between gap-3 rounded-lg border border-border bg-surface p-4 shadow-card transition-[transform,box-shadow] duration-[120ms] hover:-translate-y-px hover:shadow-elevated"
    >
      <span className="flex flex-col gap-1">
        <span className="font-semibold text-fg">{report.title}</span>
        <span className="text-sm text-fg-secondary">{report.description}</span>
      </span>
      <ChevronRight className="mt-0.5 size-4 shrink-0 text-fg-muted group-hover:text-fg" aria-hidden />
    </Link>
  );
}

const GRID = "grid grid-cols-1 gap-3 md:grid-cols-2 xl:grid-cols-3";

// The same tree as the sidebar, as cards with a line on what each report shows.
export function ReportsPage() {
  const leaves = REPORT_TREE.filter((n) => !isGroup(n)) as ReportSpec[];
  const groups = REPORT_TREE.filter(isGroup);

  return (
    <div className="flex flex-col gap-8">
      <Reveal>
        <PageHeader
          title="Reports"
          description="Ready-made views of everything recorded on your audited file servers. Pick a report to see its events, newest first."
        />
      </Reveal>
      <Reveal index={1} className="flex flex-col gap-3">
        <h2 className="text-lg font-semibold">File and folder activity</h2>
        <div className={GRID}>{leaves.map((r) => <ReportCard key={r.slug} report={r} />)}</div>
      </Reveal>
      {groups.map((g, i) => (
        <section key={g.slug} id={g.slug} className="scroll-mt-20">
          <Reveal index={i + 2} className="flex flex-col gap-3">
            <h2 className="text-lg font-semibold">{g.group}</h2>
            <div className={GRID}>{g.reports.map((r) => <ReportCard key={r.slug} report={r} />)}</div>
          </Reveal>
        </section>
      ))}
    </div>
  );
}
