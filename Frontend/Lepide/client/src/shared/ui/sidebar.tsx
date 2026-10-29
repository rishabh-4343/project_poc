"use client";

import { Suspense, useState } from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { motion, useReducedMotion } from "motion/react";
import { ChevronRight, Home } from "lucide-react";
import { BRAND_NAME } from "../brand";
import { cn } from "../cn";
import { modules, type NavItem } from "../nav";
import { useUiStore } from "@/store/ui-store";
import { Skeleton } from "./skeleton";
import { Badge } from "./badge";

const core: NavItem[] = [{ label: "Overview", href: "/", icon: Home }];

function isActive(pathname: string, href: string, exact?: boolean) {
  return exact || href === "/" ? pathname === href : pathname === href || pathname.startsWith(`${href}/`);
}

const hasActive = (item: NavItem, pathname: string): boolean =>
  isActive(pathname, item.href, item.exact) || (item.children ?? []).some((c) => hasActive(c, pathname));

// Neutral active state with a thin primary marker; the primary color is not repeated across the whole row.
function rowClass(active: boolean) {
  return cn(
    "relative flex items-center gap-3 rounded-lg px-3.5 text-sm transition-colors duration-150",
    active ? "bg-surface-2 font-semibold text-fg" : "font-medium text-fg-secondary hover:bg-surface-2 hover:text-fg",
  );
}

function Marker({ active }: { active: boolean }) {
  return active ? <span className="absolute inset-y-1.5 left-0 w-[3px] rounded-full bg-accent" aria-hidden /> : null;
}

function Chevron({ open, onClick, label }: { open: boolean; onClick: () => void; label: string }) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-expanded={open}
      aria-label={`${open ? "Collapse" : "Expand"} ${label}`}
      className="flex size-9 shrink-0 items-center justify-center rounded-lg text-fg-muted transition-colors duration-150 hover:bg-surface-2 hover:text-fg"
    >
      <ChevronRight className={cn("size-4 transition-transform duration-[120ms]", open && "rotate-90")} aria-hidden />
    </button>
  );
}

// Second-level entries: plain links, or a collapsible group of links (e.g. "Permission Reports").
function TreeNode({ item, pathname }: { item: NavItem; pathname: string }) {
  const [open, setOpen] = useState(() => hasActive(item, pathname));

  if (!item.children) {
    const active = pathname === item.href;
    return (
      <Link
        href={item.href}
        aria-current={active ? "page" : undefined}
        className={cn(
          "relative block rounded-md px-3 py-2 text-[13px] leading-snug transition-colors duration-150",
          active ? "bg-surface-2 font-semibold text-fg" : "text-fg-secondary hover:bg-surface-2 hover:text-fg",
        )}
      >
        {active ? <span className="absolute inset-y-2 -left-[13px] w-[2px] rounded-full bg-accent" aria-hidden /> : null}
        {item.label}
      </Link>
    );
  }

  return (
    <div>
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        aria-expanded={open}
        className="flex w-full items-center gap-2 rounded-md px-3 py-2 text-left text-[13px] font-medium leading-snug text-fg transition-colors duration-150 hover:bg-surface-2"
      >
        <ChevronRight className={cn("size-3.5 shrink-0 text-fg-muted transition-transform duration-150", open && "rotate-90")} aria-hidden />
        {item.label}
      </button>
      {open ? <SubTree items={item.children} pathname={pathname} /> : null}
    </div>
  );
}

function SubTree({ items, pathname }: { items: NavItem[]; pathname: string }) {
  return (
    <div className="ml-[22px] mt-1 mb-1 flex flex-col gap-1 border-l border-border pl-3">
      {items.map((child) => <TreeNode key={child.href} item={child} pathname={pathname} />)}
    </div>
  );
}

function NavEntry({ item, open }: { item: NavItem; open: boolean }) {
  const pathname = usePathname();
  const active = isActive(pathname, item.href, item.exact);
  // Branches start expanded so every report is visible from anywhere.
  const [expanded, setExpanded] = useState(true);
  const Icon = item.icon;

  const link = (
    <Link
      href={item.href}
      aria-current={active ? "page" : undefined}
      title={open ? undefined : item.label}
      className={cn(rowClass(active), "h-10 flex-1")}
    >
      <Marker active={active} />
      {Icon ? <Icon className="size-6 shrink-0" aria-hidden /> : <span className="size-4 shrink-0" aria-hidden />}
      {open ? <span className="truncate">{item.label}</span> : <span className="sr-only">{item.label}</span>}
    </Link>
  );

  if (!item.children || !open) {
    return link;
  }

  return (
    <div className="flex flex-col gap-1">
      <div className="flex items-center gap-1">
        {link}
        <Chevron open={expanded} onClick={() => setExpanded((e) => !e)} label={item.label} />
      </div>
      {expanded ? <SubTree items={item.children} pathname={pathname} /> : null}
    </div>
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
      animate={{ width: open ? 264 : 64 }}
      transition={{ duration: reduced ? 0 : 0.2, ease: "easeInOut" }}
      className="sticky top-0 flex h-dvh shrink-0 flex-col overflow-hidden border-r border-border bg-surface"
    >
      <Link href="/" className="flex h-14 shrink-0 items-center border-b border-border px-5" aria-label={`${BRAND_NAME} home`}>
        {open ? (
          <span className="truncate text-base font-semibold tracking-tight text-fg">{BRAND_NAME}</span>
        ) : (
          <span className="flex size-7 items-center justify-center rounded-md bg-surface-2 text-sm font-semibold text-fg" aria-hidden>
            {BRAND_NAME.charAt(0)}
          </span>
        )}
      </Link>

      <nav aria-label="Main" className="flex flex-1 flex-col gap-8 overflow-y-auto px-3 py-5">
        <Suspense fallback={<NavSkeleton />}>
          <div className="flex flex-col gap-1.5">
            {core.map((item) => <NavEntry key={item.href} item={item} open={open} />)}
          </div>

          {modules.map((m) => (
            <div key={m.slug} className="flex flex-col gap-3.5">
              {open ? (
                <p className="px-3.5 pb-2 text-[13px] font-semibold uppercase tracking-wider text-fg-muted">{m.label}</p>
              ) : null}
              {(m.children ?? [{ label: m.label, href: m.href }]).map((item) => (
                <NavEntry key={item.href} item={item} open={open} />
              ))}
            </div>
          ))}
          {modules.length === 0 && open ? (
            <p className="px-3 text-sm text-fg-muted">No modules registered yet.</p>
          ) : null}
        </Suspense>
      </nav>

      {open ? (
        <div className="flex shrink-0 items-center border-t border-border p-3">
          <Badge tone="neutral">{process.env.NODE_ENV === "production" ? "Production" : "Development"}</Badge>
        </div>
      ) : null}
    </motion.aside>
  );
}
