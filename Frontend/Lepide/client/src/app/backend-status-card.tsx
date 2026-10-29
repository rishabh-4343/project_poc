"use client";

import { useSyncExternalStore } from "react";
import { RefreshCw } from "lucide-react";
import { API_URL } from "@/shared/config";
import { useHealth } from "@/shared/hooks/use-health";
import { Badge } from "@/shared/ui/badge";
import { Button } from "@/shared/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/shared/ui/card";
import { Skeleton } from "@/shared/ui/skeleton";

const noop = () => () => {};

export function BackendStatusCard() {
  const health = useHealth();
  const { data, isError, isFetching, refetch } = health;
  // The top bar shares this query and usually has an answer before this card hydrates; render the server's skeleton
  // until hydration is done so the markup matches.
  const hydrated = useSyncExternalStore(noop, () => true, () => false);
  const isPending = health.isPending || !hydrated;

  return (
    <Card>
      <CardHeader>
        <div className="flex items-center justify-between gap-3">
          <CardTitle>Audit service</CardTitle>
          {isPending ? (
            <Skeleton className="h-6 w-20" />
          ) : isError ? (
            <Badge tone="danger">Offline</Badge>
          ) : (
            <Badge tone="success">Online</Badge>
          )}
        </div>
        <CardDescription>
          <span className="font-mono text-xs">{API_URL}</span>
        </CardDescription>
      </CardHeader>
      <CardContent className="flex items-center justify-between gap-3">
        {isPending ? (
          <Skeleton className="h-4 w-48" />
        ) : isError ? (
          <p className="text-sm text-fg-muted">
            Can&apos;t reach the audit service. Check that it is running
            and that this console points at the right address.
          </p>
        ) : (
          <p className="text-sm text-fg-muted tabular-nums">Last checked {data?.toLocaleTimeString()}</p>
        )}
        <Button variant="secondary" size="sm" onClick={() => refetch()} disabled={isFetching}>
          <RefreshCw className={isFetching ? "animate-spin" : undefined} aria-hidden />
          Check now
        </Button>
      </CardContent>
    </Card>
  );
}
