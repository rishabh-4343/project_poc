"use client";

import { useEffect, useState } from "react";
import { animate, useReducedMotion } from "motion/react";
import { Area, AreaChart, ResponsiveContainer } from "recharts";
import { ArrowDownRight, ArrowUpRight, Minus } from "lucide-react";
import { cn } from "../cn";
import { Card } from "./card";
import { Skeleton } from "./skeleton";

function useCountUp(target: number) {
  const reduced = useReducedMotion();
  const [value, setValue] = useState(0);

  useEffect(() => {
    if (reduced) {
      return;
    }
    const controls = animate(0, target, { duration: 0.6, ease: "easeOut", onUpdate: (v) => setValue(Math.round(v)) });
    return () => controls.stop();
  }, [target, reduced]);

  return reduced ? target : value;
}

type MetricCardProps = {
  label: string;
  value: number | undefined;
  // Previous period's value; the delta is hidden when unknown.
  previous?: number | null;
  spark?: number[];
  tone?: "accent" | "danger";
};

export function MetricCard({ label, value, previous, spark, tone = "accent" }: MetricCardProps) {
  const shown = useCountUp(value ?? 0);
  const delta = value !== undefined && previous != null ? value - previous : null;

  return (
    <Card className="flex flex-col gap-3 p-5">
      <p className="text-sm text-fg-muted">{label}</p>
      <div className="flex items-end justify-between gap-3">
        {value === undefined ? (
          <Skeleton className="h-9 w-20" />
        ) : (
          <p className="text-[28px] font-semibold leading-none tabular-nums tracking-tight">{shown.toLocaleString()}</p>
        )}
        {spark && spark.length > 1 ? (
          <div className="h-9 w-24" aria-hidden>
            <ResponsiveContainer width="100%" height="100%">
              <AreaChart data={spark.map((v, i) => ({ i, v }))}>
                <Area
                  type="monotone"
                  dataKey="v"
                  stroke={tone === "danger" ? "var(--danger)" : "var(--accent)"}
                  fill={tone === "danger" ? "var(--danger-soft)" : "var(--accent-soft)"}
                  strokeWidth={1.5}
                  isAnimationActive={false}
                />
              </AreaChart>
            </ResponsiveContainer>
          </div>
        ) : null}
      </div>
      <p className={cn("flex items-center gap-1 text-xs tabular-nums", delta == null || delta === 0 ? "text-fg-muted" : delta > 0 ? "text-success" : "text-danger")}>
        {delta == null ? null : delta === 0 ? <Minus className="size-3.5" aria-hidden /> : delta > 0 ? <ArrowUpRight className="size-3.5" aria-hidden /> : <ArrowDownRight className="size-3.5" aria-hidden />}
        {delta == null ? "No earlier data" : `${delta > 0 ? "+" : ""}${delta.toLocaleString()} vs yesterday`}
      </p>
    </Card>
  );
}
