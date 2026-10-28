"use client";

import { Suspense } from "react";
import { usePathname } from "next/navigation";
import { ChevronRight, PanelLeft } from "lucide-react";
import { getSession } from "../auth";
import { flattenNav, modules } from "../nav";
import { useUiStore } from "@/store/ui-store";
import { Button } from "./button";
import { AgentsPill } from "./agents-pill";
import { HealthPill } from "./health-pill";
import { Skeleton } from "./skeleton";
import { ThemeToggle } from "./theme-toggle";

const SEGMENT_LABELS: Record<string, string> = { new: "New audit" };

function titleFor(segment: string, href: string) {
  const entry = modules.find((m) => m.href === href) ?? flattenNav().find((i) => i.href === href);
  if (entry) {
    return entry.label;
  }
  if (/^[0-9a-f]{8}-[0-9a-f]{4}-/i.test(segment)) {
    return "Details";
  }
  return SEGMENT_LABELS[segment] ?? segment.charAt(0).toUpperCase() + segment.slice(1).replace(/-/g, " ");
}

function Breadcrumb() {
  const pathname = usePathname();
  const segments = pathname.split("/").filter(Boolean);
  const crumbs =
    segments.length === 0
      ? [{ label: "Overview", href: "/" }]
      : segments.map((segment, i) => {
          const href = `/${segments.slice(0, i + 1).join("/")}`;
          return { label: titleFor(segment, href), href };
        });

  return (
    <nav aria-label="Breadcrumb" className="flex items-center gap-1.5 text-sm">
      {crumbs.map((crumb, i) => (
        <span key={crumb.href} className="flex items-center gap-1.5">
          {i > 0 ? <ChevronRight className="size-3.5 text-fg-muted" aria-hidden /> : null}
          <span className={i === crumbs.length - 1 ? "font-medium" : "text-fg-muted"}>{crumb.label}</span>
        </span>
      ))}
    </nav>
  );
}

export function Topbar() {
  const toggleSidebar = useUiStore((s) => s.toggleSidebar);
  const session = getSession();
  const initials = session.displayName
    .split(" ")
    .map((part) => part[0])
    .join("")
    .slice(0, 2)
    .toUpperCase();

  return (
    <header className="sticky top-0 z-10 flex h-14 shrink-0 items-center justify-between gap-4 border-b border-border bg-surface/90 px-6 backdrop-blur">
      <div className="flex items-center gap-3">
        <Button variant="ghost" size="icon" onClick={toggleSidebar} aria-label="Toggle sidebar" title="Toggle sidebar">
          <PanelLeft aria-hidden />
        </Button>
        <Suspense fallback={<Skeleton className="h-4 w-32" />}>
          <Breadcrumb />
        </Suspense>
      </div>

      <div className="flex items-center gap-3">
        <AgentsPill />
        <HealthPill />
        <ThemeToggle />
        <div
          className="flex size-8 items-center justify-center rounded-full bg-surface-2 text-xs font-semibold text-fg-secondary"
          title={`${session.displayName} (${session.email})`}
          aria-label={`Signed in as ${session.displayName}`}
        >
          {initials}
        </div>
      </div>
    </header>
  );
}
