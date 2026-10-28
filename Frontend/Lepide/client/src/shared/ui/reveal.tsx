"use client";

import * as React from "react";
import { motion, useReducedMotion } from "motion/react";

// Section entrance from the UI spec: fade and rise 8px, 200-300ms, ease-out, staggered by `index`.
export function Reveal({
  children,
  index = 0,
  className,
}: {
  children: React.ReactNode;
  index?: number;
  className?: string;
}) {
  const reduced = useReducedMotion();

  return (
    <motion.div
      className={className}
      initial={{ opacity: 0, y: reduced ? 0 : 8 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: 0.25, ease: "easeOut", delay: index * 0.04 }}
    >
      {children}
    </motion.div>
  );
}
