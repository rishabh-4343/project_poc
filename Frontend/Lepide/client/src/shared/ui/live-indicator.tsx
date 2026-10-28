"use client";

import { motion, useReducedMotion } from "motion/react";
import { cn } from "../cn";

// Pulsing dot with a tiny spring, per the UI spec's only allowed spring.
export function LiveIndicator({ label = "Recording" }: { label?: string }) {
  const reduced = useReducedMotion();

  return (
    <span className="inline-flex items-center gap-2 text-sm font-medium text-danger">
      <span className="relative flex size-2.5">
        {reduced ? null : (
          <motion.span
            className="absolute inline-flex size-full rounded-full bg-danger"
            animate={{ scale: [1, 2.2], opacity: [0.6, 0] }}
            transition={{ duration: 1.4, repeat: Infinity, ease: "easeOut" }}
          />
        )}
        <span className="relative inline-flex size-2.5 rounded-full bg-danger" />
      </span>
      {label}
    </span>
  );
}

// The not-recording counterpart: a still dot, so a paused or degraded session never looks live.
export function PausedIndicator({ label, tone = "warning", title }: { label: string; tone?: "warning" | "muted"; title?: string }) {
  return (
    <span
      className={cn("inline-flex items-center gap-2 text-sm font-medium", tone === "warning" ? "text-warning" : "text-fg-muted")}
      title={title}
    >
      <span className={cn("inline-flex size-2.5 rounded-full", tone === "warning" ? "bg-warning" : "bg-fg-muted")} aria-hidden />
      {label}
    </span>
  );
}
