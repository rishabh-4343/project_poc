import * as React from "react";
import type { LucideIcon } from "lucide-react";
import { cn } from "../cn";

type EmptyStateProps = {
  icon: LucideIcon;
  title: string;
  description?: string;
  action?: React.ReactNode;
  className?: string;
};

export function EmptyState({ icon: Icon, title, description, action, className }: EmptyStateProps) {
  return (
    <div
      className={cn(
        "flex flex-col items-center gap-3 rounded-lg border border-dashed border-border px-6 py-12 text-center",
        className,
      )}
    >
      <div className="flex size-11 items-center justify-center rounded-lg bg-surface-2 text-fg-muted">
        <Icon className="size-5" aria-hidden />
      </div>
      <div className="flex flex-col gap-1">
        <p className="text-base font-semibold">{title}</p>
        {description ? <p className="max-w-md text-sm text-fg-secondary">{description}</p> : null}
      </div>
      {action}
    </div>
  );
}
