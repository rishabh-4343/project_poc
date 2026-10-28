"use client";

import { Suspense } from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { motion, useReducedMotion } from "motion/react";
import { Home, ShieldCheck, type LucideIcon } from "lucide-react";
import { cn } from "../cn";
import { modules } from "../nav";
import { useUiStore } from "@/store/ui-store";
import { Skeleton } from "./skeleton";
import { ThemeToggle } from "./theme-toggle";
import { Badge } from "./badge";

const core = [{ label: "Overview", href: "/", icon: Home }];

function isActive(pathname: string, href: string, exact?: boolean) {
  return exact || href === "/" ? pathname === href : pathname === href || pathname.startsWith(`${href}/`);
}

function NavLink({
  href,
  label,
  icon: Icon,
  exact,
  open,
}: {
  href: string;
  label: string;
  icon?: LucideIcon;
  exact?: boolean;
  open: boolean;
}) {
  const active = isActive(usePathname(), href, exact);

  return (
    <Link
      href={href}
      aria-current={active ? "page" : undefined}
      title={open ? undefined : label}
      className={cn(
        "relative flex h-9 items-center gap-3 rounded-md px-3 text-sm font-medium transition-colors duration-[120ms]",
        active ? "bg-accent-soft text-accent" : "text-fg-muted hover:bg-surface-2 hover:text-fg",
      )}
    >
      {active ? <span className="absolute inset-y-1.5 left-0 w-[3px] rounded-full bg-accent" aria-hidden /> : null}
      {Icon ? <Icon className="size-4 shrink-0" aria-hidden /> : <span className="size-4 shrink-0" aria-hidden />}
      {open ? <span className="truncate">{label}</span> : <span className="sr-only">{label}</span>}
    </Link>
  );
}

// usePathname suspends under dynamic routes with Cache Components, so the links sit behind Suspense.
function NavSkeleton() {
  return (
    <div className="flex flex-col gap-2 px-3">
      <Skeleton className="h-9" />
      <Skeleton className="h-9" />
    </div>
  );
}

export function Sidebar() {
  const open = useUiStore((s) => s.sidebarOpen);
  const reduced = useReducedMotion();

  return (
    <motion.aside
      initial={false}
      animate={{ width: open ? 240 : 64 }}
      transition={{ duration: reduced ? 0 : 0.2, ease: "easeInOut" }}
      className="sticky top-0 flex h-dvh shrink-0 flex-col overflow-hidden border-r border-border bg-surface"
    >
      <div className="flex h-14 shrink-0 items-center gap-2.5 border-b border-border px-[18px]">
        <ShieldCheck className="size-6 shrink-0 text-accent" aria-hidden />
        {open ? <span className="text-base font-semibold tracking-tight">Auditor</span> : null}
      </div>

      <nav aria-label="Main" className="flex flex-1 flex-col gap-6 overflow-y-auto p-3">
        <Suspense fallback={<NavSkeleton />}>
          <div className="flex flex-col gap-1">
            {core.map((item) => (
              <NavLink key={item.href} {...item} open={open} />
            ))}
          </div>

          {modules.map((m) => (
            <div key={m.slug} className="flex flex-col gap-1">
              {open ? (
                <p className="px-3 pb-1 text-xs font-medium uppercase tracking-wide text-fg-muted">{m.label}</p>
              ) : null}
              {(m.children ?? [{ label: m.label, href: m.href }]).map((item) => (
                <NavLink key={item.href} {...item} open={open} />
              ))}
            </div>
          ))}
          {modules.length === 0 && open ? (
            <p className="px-3 text-sm text-fg-muted">No modules registered yet.</p>
          ) : null}
        </Suspense>
      </nav>

      <div className="flex shrink-0 items-center justify-between gap-2 border-t border-border p-3">
        {open ? (
          <Badge tone="neutral">{process.env.NODE_ENV === "production" ? "Production" : "Development"}</Badge>
        ) : null}
        <ThemeToggle />
      </div>
    </motion.aside>
  );
}
