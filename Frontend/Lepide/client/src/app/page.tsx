import Link from "next/link";
import { Boxes } from "lucide-react";
import { modules } from "@/shared/nav";
import { Card, CardDescription, CardHeader, CardTitle } from "@/shared/ui/card";
import { EmptyState } from "@/shared/ui/empty-state";
import { PageHeader } from "@/shared/ui/page-header";
import { Reveal } from "@/shared/ui/reveal";
import { BackendStatusCard } from "./backend-status-card";

export default function OverviewPage() {
  return (
    <div className="flex flex-col gap-8">
      <Reveal>
        <PageHeader title="Overview" description="Status of the Auditor platform and the modules installed on it." />
      </Reveal>

      <Reveal index={1}>
        <BackendStatusCard />
      </Reveal>

      <Reveal index={2} className="flex flex-col gap-3">
        <h2 className="text-lg font-semibold">Modules</h2>
        {modules.length === 0 ? (
          <EmptyState
            icon={Boxes}
            title="No modules registered"
            description="Modules appear here and in the sidebar once they add an entry to the nav registry."
          />
        ) : (
          <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3">
            {modules.map((m) => (
              <Link key={m.slug} href={m.href} className="rounded-lg transition-transform duration-[120ms] hover:-translate-y-px">
                <Card className="h-full transition-shadow hover:shadow-elevated">
                  <CardHeader>
                    <CardTitle>{m.label}</CardTitle>
                    <CardDescription className="font-mono text-xs">{m.href}</CardDescription>
                  </CardHeader>
                </Card>
              </Link>
            ))}
          </div>
        )}
      </Reveal>
    </div>
  );
}
