"use client";

import { motion, useReducedMotion } from "motion/react";

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
