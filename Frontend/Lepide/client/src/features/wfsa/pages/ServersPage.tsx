"use client";

import { useState } from "react";
import { Check, Plus, Server as ServerIcon } from "lucide-react";
import { toast } from "sonner";
import type { ColumnDef } from "@tanstack/react-table";
import { formatDateTime } from "@/shared/format";
import { Badge } from "@/shared/ui/badge";
import { Button } from "@/shared/ui/button";
import { DataTable } from "@/shared/ui/data-table";
import { Dialog, DialogContent, DialogTrigger } from "@/shared/ui/dialog";
import { EmptyState } from "@/shared/ui/empty-state";
import { Field, Input, Select } from "@/shared/ui/input";
import { PageHeader } from "@/shared/ui/page-header";
import { Reveal } from "@/shared/ui/reveal";
import { useAgents, useApproveServer, useRegisterServer, useServers } from "../hooks";
import type { Server } from "../types";

function RegisterServerDrawer() {
  const agents = useAgents();
  const register = useRegisterServer();
  const [open, setOpen] = useState(false);
  const [agentId, setAgentId] = useState("");
  const [hostName, setHostName] = useState("");
  const [osVersion, setOsVersion] = useState("");

  const valid = agentId && hostName.trim();

  function submit(e: React.FormEvent) {
    e.preventDefault();
    register.mutate(
      { agentId, hostName: hostName.trim(), osVersion: osVersion.trim() || undefined },
      {
        onSuccess: (s) => {
          toast.success(`Registered ${s.hostName}. Approve it to start auditing.`);
          setOpen(false);
          setHostName("");
          setOsVersion("");
        },
      },
    );
  }

  return (
    <Dialog open={open} onOpenChange={(o) => { setOpen(o); register.reset(); }}>
      <DialogTrigger asChild>
        <Button><Plus aria-hidden />Register server</Button>
      </DialogTrigger>
      <DialogContent side="right" title="Register server" description="Link a file server to the agent that runs on it.">
        <form onSubmit={submit} className="flex flex-1 flex-col gap-4">
          <Field label="Agent">
            <Select value={agentId} onChange={(e) => setAgentId(e.target.value)} required>
              <option value="">Select an agent…</option>
              {agents.data?.map((a) => (
                <option key={a.id} value={a.id}>{a.name}{a.online ? " (online)" : ""}</option>
              ))}
            </Select>
          </Field>
          <Field label="Host name" hint="Must be unique, e.g. fs01.">
            <Input value={hostName} onChange={(e) => setHostName(e.target.value)} placeholder="fs01" required />
          </Field>
          <Field label="OS version" hint="Optional.">
            <Input value={osVersion} onChange={(e) => setOsVersion(e.target.value)} placeholder="Windows Server 2022" />
          </Field>
          {register.isError ? <p className="text-sm text-danger" role="alert">{register.error.message}</p> : null}
          <div className="mt-auto flex justify-end gap-2">
            <Button type="button" variant="secondary" onClick={() => setOpen(false)}>Cancel</Button>
            <Button type="submit" disabled={!valid || register.isPending}>{register.isPending ? "Registering…" : "Register"}</Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  );
}

export function ServersPage() {
  const servers = useServers();
  const agents = useAgents();
  const approve = useApproveServer();
  const agentName = new Map(agents.data?.map((a) => [a.id, a.name]));

  const columns: ColumnDef<Server>[] = [
    { header: "Host", cell: ({ row }) => <span className="font-medium">{row.original.hostName}</span> },
    { header: "Agent", cell: ({ row }) => agentName.get(row.original.agentId) ?? "—" },
    { header: "OS", cell: ({ row }) => <span className="text-fg-muted">{row.original.osVersion ?? "—"}</span> },
    {
      header: "Approval",
      cell: ({ row }) =>
        row.original.approved ? <Badge tone="success">Approved</Badge> : <Badge tone="warning">Awaiting approval</Badge>,
    },
    { header: "Registered", cell: ({ row }) => <span className="text-fg-muted">{formatDateTime(row.original.createdAt)}</span> },
    {
      header: "",
      id: "actions",
      cell: ({ row }) =>
        row.original.approved ? null : (
          <Button size="sm" variant="secondary" disabled={approve.isPending} onClick={() => approve.mutate(row.original.id)}>
            <Check aria-hidden />Approve
          </Button>
        ),
    },
  ];

  return (
    <div className="flex flex-col gap-6">
      <Reveal>
        <PageHeader title="Servers" description="File servers that can be audited. A server must be approved before a session can run on it." actions={<RegisterServerDrawer />} />
      </Reveal>
      <Reveal index={1}>
        {servers.isError ? (
          <p className="text-sm text-danger">{servers.error.message}</p>
        ) : (
          <DataTable
            columns={columns}
            data={servers.data ?? []}
            getRowId={(s) => s.id}
            loading={servers.isPending}
            empty={<EmptyState icon={ServerIcon} title="No servers registered" description="Register a server to link it to an agent, then approve it." action={<RegisterServerDrawer />} />}
          />
        )}
      </Reveal>
    </div>
  );
}
