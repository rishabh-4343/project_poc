"use client";

import { useState } from "react";
import { Check, Copy, Plug, Plus, Trash2 } from "lucide-react";
import { toast } from "sonner";
import type { ColumnDef } from "@tanstack/react-table";
import { formatRelative } from "@/shared/format";
import { Badge } from "@/shared/ui/badge";
import { Button } from "@/shared/ui/button";
import { DataTable } from "@/shared/ui/data-table";
import { Dialog, DialogClose, DialogContent, DialogTrigger } from "@/shared/ui/dialog";
import { EmptyState } from "@/shared/ui/empty-state";
import { Field, Input } from "@/shared/ui/input";
import { PageHeader } from "@/shared/ui/page-header";
import { Reveal } from "@/shared/ui/reveal";
import { useAgents, useRegisterAgent, useRevokeAgent, useSessionUpdates } from "../hooks";
import type { Agent, RegisteredAgent } from "../types";

// The key is only returned by the registration call, so this dialog is the one place it can be copied.
function AddAgentDrawer() {
  const register = useRegisterAgent();
  const [open, setOpen] = useState(false);
  const [name, setName] = useState("");
  const [created, setCreated] = useState<RegisteredAgent | null>(null);
  const [copied, setCopied] = useState(false);

  function close(next: boolean) {
    setOpen(next);
    if (!next) {
      setName("");
      setCreated(null);
      setCopied(false);
      register.reset();
    }
  }

  async function copy(key: string) {
    try {
      await navigator.clipboard.writeText(key);
      setCopied(true);
    } catch {
      toast.error("Could not copy. Select the key and copy it by hand.");
    }
  }

  return (
    <Dialog open={open} onOpenChange={close}>
      <DialogTrigger asChild>
        <Button><Plus aria-hidden />Add agent</Button>
      </DialogTrigger>
      <DialogContent
        side="right"
        title={created ? "Agent created" : "Add agent"}
        description={created ? "Copy the key now. It is not stored and cannot be shown again." : "Create a key for an agent that will run on a file server."}
      >
        {created ? (
          <div className="flex flex-1 flex-col gap-4">
            <Field label="Agent key" hint="Set it on the server as the Agent__ApiKey environment variable.">
              <div className="flex gap-2">
                <Input readOnly value={created.apiKey} className="font-mono text-xs" onFocus={(e) => e.currentTarget.select()} />
                <Button type="button" variant="secondary" onClick={() => copy(created.apiKey)}>
                  {copied ? <Check aria-hidden /> : <Copy aria-hidden />}{copied ? "Copied" : "Copy"}
                </Button>
              </div>
            </Field>
            <div className="mt-auto flex justify-end">
              <Button onClick={() => close(false)}>Done</Button>
            </div>
          </div>
        ) : (
          <form
            className="flex flex-1 flex-col gap-4"
            onSubmit={(e) => {
              e.preventDefault();
              register.mutate(name.trim(), { onSuccess: setCreated });
            }}
          >
            <Field label="Name" hint="For example the server it will run on.">
              <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="fs01-agent" maxLength={100} required />
            </Field>
            {register.isError ? <p className="text-sm text-danger" role="alert">{register.error.message}</p> : null}
            <div className="mt-auto flex justify-end gap-2">
              <Button type="button" variant="secondary" onClick={() => close(false)}>Cancel</Button>
              <Button type="submit" disabled={!name.trim() || register.isPending}>{register.isPending ? "Creating…" : "Create"}</Button>
            </div>
          </form>
        )}
      </DialogContent>
    </Dialog>
  );
}

function RevokeButton({ agent }: { agent: Agent }) {
  const revoke = useRevokeAgent();
  const [open, setOpen] = useState(false);

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger asChild>
        <Button size="sm" variant="ghost"><Trash2 aria-hidden />Revoke</Button>
      </DialogTrigger>
      <DialogContent title={`Revoke ${agent.name}?`} description="Its key stops working. Servers linked to it keep their history but are no longer audited.">
        <div className="flex justify-end gap-2">
          <DialogClose asChild><Button variant="secondary">Keep</Button></DialogClose>
          <Button variant="danger" disabled={revoke.isPending} onClick={() => revoke.mutate(agent.id, { onSuccess: () => setOpen(false) })}>
            Revoke agent
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  );
}

const columns: ColumnDef<Agent>[] = [
  { header: "Agent", accessorKey: "name", cell: ({ row }) => <span className="font-medium">{row.original.name}</span> },
  {
    header: "Status",
    cell: ({ row }) =>
      row.original.online ? (
        <Badge tone="success"><span className="size-1.5 rounded-full bg-success" aria-hidden />Online</Badge>
      ) : (
        <Badge tone="neutral"><span className="size-1.5 rounded-full bg-fg-muted" aria-hidden />Offline</Badge>
      ),
  },
  { header: "Last seen", cell: ({ row }) => <span className="text-fg-muted">{formatRelative(row.original.lastSeenAt)}</span> },
  { header: "Agent version", cell: ({ row }) => <span className="font-mono text-xs">{row.original.agentVersion ?? "—"}</span> },
  { header: "Driver version", cell: ({ row }) => <span className="font-mono text-xs">{row.original.driverVersion ?? "—"}</span> },
  { header: "", id: "actions", cell: ({ row }) => <RevokeButton agent={row.original} /> },
];

export function AgentsPage() {
  const { data, isPending, isError, error } = useAgents();
  useSessionUpdates();

  return (
    <div className="flex flex-col gap-6">
      <Reveal>
        <PageHeader title="Agents" description="Collectors installed on file servers. Status updates live." actions={<AddAgentDrawer />} />
      </Reveal>
      <Reveal index={1}>
        {isError ? (
          <p className="text-sm text-danger">{error.message}</p>
        ) : (
          <DataTable
            columns={columns}
            data={data ?? []}
            getRowId={(a) => a.id}
            loading={isPending}
            empty={
              <EmptyState
                icon={Plug}
                title="No agents yet"
                description="Add an agent to get a key, then set the key on the file server where the agent runs."
                action={<AddAgentDrawer />}
              />
            }
          />
        )}
      </Reveal>
    </div>
  );
}
