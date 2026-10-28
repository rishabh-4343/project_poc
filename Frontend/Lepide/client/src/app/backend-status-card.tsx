"use client";

import { RefreshCw } from "lucide-react";
import { API_URL } from "@/shared/config";
import { useHealth } from "@/shared/hooks/use-health";
import { Badge } from "@/shared/ui/badge";
import { Button } from "@/shared/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/shared/ui/card";
import { Skeleton } from "@/shared/ui/skeleton";

export function BackendStatusCard() {
  const { data, isPending, isError, isFetching, refetch } = useHealth();

  return (
    <Card>
      <CardHeader>
        <div className="flex items-center justify-between gap-3">
          <CardTitle>Backend</CardTitle>
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
            Can&apos;t reach the API. Start it with <span className="font-mono text-xs">dotnet run</span> in Backend/Auditing/server/server,
            and check NEXT_PUBLIC_API_URL.
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
