"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { AlertTriangle, FolderPlus, Server as ServerIcon, X } from "lucide-react";
import { toast } from "sonner";
import { cn } from "@/shared/cn";
import { Badge } from "@/shared/ui/badge";
import { Button } from "@/shared/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/shared/ui/card";
import { EmptyState } from "@/shared/ui/empty-state";
import { Checkbox, Field, Input } from "@/shared/ui/input";
import { PageHeader } from "@/shared/ui/page-header";
import { Reveal } from "@/shared/ui/reveal";
import { Skeleton } from "@/shared/ui/skeleton";
import { useAgents, useCreateSession, useServers, useShares } from "../hooks";
import { DEFAULT_OPERATIONS, OPERATION_GROUPS } from "../types";

type FolderDraft = { localPath: string; shareId?: string; recursive: boolean };

export function NewSessionPage() {
  const router = useRouter();
  const servers = useServers();
  const agents = useAgents();
  const shares = useShares();
  const create = useCreateSession();

  const [serverId, setServerId] = useState("");
  const [name, setName] = useState("");
  const [folders, setFolders] = useState<FolderDraft[]>([]);
  const [path, setPath] = useState("");
  const [operations, setOperations] = useState<Set<string>>(new Set(DEFAULT_OPERATIONS));
  const [includeReads, setIncludeReads] = useState(false);

  const online = new Map(agents.data?.map((a) => [a.id, a.online]));
  const serverShares = (shares.data ?? []).filter((s) => s.serverId === serverId);

  const hasFolder = (p: string) => folders.some((f) => f.localPath.toLowerCase() === p.toLowerCase());

  function addFolder(localPath: string, shareId?: string) {
    const trimmed = localPath.trim();
    if (!trimmed || hasFolder(trimmed)) return;
    setFolders((f) => [...f, { localPath: trimmed, shareId, recursive: true }]);
    setPath("");
  }

  function toggleOperation(op: string) {
    setOperations((prev) => {
      const next = new Set(prev);
      if (!next.delete(op)) next.add(op);
      return next;
    });
  }

  const problem = !serverId
    ? "Choose a server."
    : folders.length === 0
      ? "Add at least one folder to watch."
      : operations.size === 0
        ? "Tick at least one operation."
        : null;

  function start() {
    create.mutate(
      {
        serverId,
        name: name.trim() || undefined,
        operations: [...operations],
        includeReads,
        folders: folders.map((f) => ({ localPath: f.localPath, shareId: f.shareId, recursive: f.recursive })),
      },
      {
        onSuccess: (s) => {
          toast.success("Session requested");
          router.push(`/wfsa/sessions/${s.id}`);
        },
      },
    );
  }

  return (
    <div className="flex max-w-3xl flex-col gap-8">
      <Reveal>
        <PageHeader title="New session" description="Pick a server, the folders to watch, and which operations to record." />
      </Reveal>

      <Reveal index={1}>
        <Card>
          <CardHeader>
            <CardTitle>1. Server</CardTitle>
            <CardDescription>Only approved servers can be audited.</CardDescription>
          </CardHeader>
          <CardContent>
            {servers.isPending ? (
              <Skeleton className="h-20 w-full" />
            ) : (servers.data ?? []).length === 0 ? (
              <EmptyState icon={ServerIcon} title="No servers yet" description="Register and approve a server first." />
            ) : (
              <div className="grid grid-cols-1 gap-3 sm:grid-cols-2" role="radiogroup" aria-label="Server">
                {servers.data!.map((s) => (
                  <button
                    key={s.id}
                    type="button"
                    role="radio"
                    aria-checked={serverId === s.id}
                    disabled={!s.approved}
                    onClick={() => { setServerId(s.id); setFolders([]); }}
                    className={cn(
                      "flex flex-col items-start gap-2 rounded-lg border p-4 text-left transition-colors duration-[120ms] disabled:cursor-not-allowed disabled:opacity-60",
                      serverId === s.id ? "border-accent bg-accent-soft" : "border-border hover:bg-surface-2",
                    )}
                  >
                    <span className="font-medium">{s.hostName}</span>
                    <span className="flex flex-wrap gap-1.5">
                      {s.approved ? (
                        online.get(s.agentId) ? <Badge tone="success">Agent online</Badge> : <Badge tone="warning">Agent offline</Badge>
                      ) : (
                        <Badge tone="warning">Awaiting approval</Badge>
                      )}
                      {s.osVersion ? <Badge tone="neutral">{s.osVersion}</Badge> : null}
                    </span>
                  </button>
                ))}
              </div>
            )}
          </CardContent>
        </Card>
      </Reveal>

      <Reveal index={2}>
        <Card className={cn(!serverId && "opacity-60")}>
          <CardHeader>
            <CardTitle>2. Folders</CardTitle>
            <CardDescription>Subfolders are included. Paths are as seen on the server.</CardDescription>
          </CardHeader>
          <CardContent className="flex flex-col gap-4">
            <form className="flex gap-2" onSubmit={(e) => { e.preventDefault(); addFolder(path); }}>
              <Input value={path} onChange={(e) => setPath(e.target.value)} placeholder="D:\Finance\Confidential" className="font-mono" disabled={!serverId} aria-label="Folder path" />
              <Button type="submit" variant="secondary" disabled={!serverId || !path.trim()}><FolderPlus aria-hidden />Add</Button>
            </form>

            {serverShares.length > 0 ? (
              <div className="flex flex-wrap items-center gap-2">
                <span className="text-sm text-fg-muted">Shares:</span>
                {serverShares.map((s) => (
                  <Button key={s.id} size="sm" variant="secondary" disabled={hasFolder(s.localPath)} onClick={() => addFolder(s.localPath, s.id)}>
                    {s.name}
                  </Button>
                ))}
              </div>
            ) : null}

            {folders.length > 0 ? (
              <ul className="flex flex-wrap gap-2">
                {folders.map((f) => (
                  <li key={f.localPath} className="flex items-center gap-1.5 rounded-md bg-surface-2 py-1 pl-3 pr-1 font-mono text-xs">
                    {f.localPath}
                    <button
                      type="button"
                      aria-label={`Remove ${f.localPath}`}
                      className="rounded p-1 text-fg-muted transition-colors hover:bg-border hover:text-fg"
                      onClick={() => setFolders((all) => all.filter((x) => x !== f))}
                    >
                      <X className="size-3.5" aria-hidden />
                    </button>
                  </li>
                ))}
              </ul>
            ) : null}
          </CardContent>
        </Card>
      </Reveal>

      <Reveal index={3}>
        <Card>
          <CardHeader>
            <CardTitle>3. Operations</CardTitle>
            <CardDescription>What to record inside those folders.</CardDescription>
          </CardHeader>
          <CardContent className="flex flex-col gap-5">
            {OPERATION_GROUPS.map((g) => (
              <fieldset key={g.label} className="flex flex-col gap-2">
                <legend className="mb-1 text-sm font-medium">{g.label}</legend>
                <div className="grid grid-cols-2 gap-x-4 gap-y-2 sm:grid-cols-3">
                  {g.operations.map((op) => (
                    <label key={op} className="flex items-center gap-2 text-sm">
                      <Checkbox checked={operations.has(op)} onChange={() => toggleOperation(op)} />
                      {op.replace(/([a-z])([A-Z])/g, "$1 $2")}
                    </label>
                  ))}
                </div>
              </fieldset>
            ))}

            <fieldset className="flex flex-col gap-2">
              <legend className="mb-1 flex items-center gap-2 text-sm font-medium">
                Reads <Badge tone="warning">High volume</Badge>
              </legend>
              <label className="flex items-start gap-2 text-sm">
                <Checkbox className="mt-0.5" checked={includeReads} onChange={(e) => setIncludeReads(e.target.checked)} />
                <span>
                  Include content views (file reads)
                  <span className="block text-xs text-fg-muted">Merged to one entry per user, file and hour, but still much more data. Off by default.</span>
                </span>
              </label>
            </fieldset>
          </CardContent>
        </Card>
      </Reveal>

      <Reveal index={4} className="flex flex-col gap-4">
        <Field label="Session name" hint="Optional, shown in the sessions list.">
          <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="Finance share audit" />
        </Field>

        {create.isError ? (
          <p className="flex items-center gap-2 text-sm text-danger" role="alert">
            <AlertTriangle className="size-4" aria-hidden />
            {create.error.message}
          </p>
        ) : problem ? (
          <p className="text-sm text-fg-muted">{problem}</p>
        ) : null}

        <div className="flex gap-2">
          <Button onClick={start} disabled={problem !== null || create.isPending}>
            {create.isPending ? "Starting…" : "Start session"}
          </Button>
          <Button variant="secondary" onClick={() => router.push("/wfsa/sessions")}>Cancel</Button>
        </div>
      </Reveal>
    </div>
  );
}
