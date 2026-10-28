"use client";

import { useState } from "react";
import { FolderTree, Plus } from "lucide-react";
import { toast } from "sonner";
import type { ColumnDef } from "@tanstack/react-table";
import { Badge } from "@/shared/ui/badge";
import { Button } from "@/shared/ui/button";
import { DataTable } from "@/shared/ui/data-table";
import { Dialog, DialogContent, DialogTrigger } from "@/shared/ui/dialog";
import { EmptyState } from "@/shared/ui/empty-state";
import { Field, Input, Select } from "@/shared/ui/input";
import { PageHeader } from "@/shared/ui/page-header";
import { Reveal } from "@/shared/ui/reveal";
import { useRegisterShare, useServers, useShares } from "../hooks";
import type { Share } from "../types";

function RegisterShareDrawer() {
  const servers = useServers();
  const register = useRegisterShare();
  const [open, setOpen] = useState(false);
  const [serverId, setServerId] = useState("");
  const [name, setName] = useState("");
  const [localPath, setLocalPath] = useState("");
  const [uncPath, setUncPath] = useState("");

  const valid = serverId && name.trim() && localPath.trim() && uncPath.trim();

  function submit(e: React.FormEvent) {
    e.preventDefault();
    register.mutate(
      { serverId, name: name.trim(), localPath: localPath.trim(), uncPath: uncPath.trim() },
      {
        onSuccess: (s) => {
          toast.success(`Registered share ${s.name}`);
          setOpen(false);
          setName("");
          setLocalPath("");
          setUncPath("");
        },
      },
    );
  }

  return (
    <Dialog open={open} onOpenChange={(o) => { setOpen(o); register.reset(); }}>
      <DialogTrigger asChild>
        <Button><Plus aria-hidden />Register share</Button>
      </DialogTrigger>
      <DialogContent side="right" title="Register share" description="A shared folder on one of your servers.">
        <form onSubmit={submit} className="flex flex-1 flex-col gap-4">
          <Field label="Server">
            <Select value={serverId} onChange={(e) => setServerId(e.target.value)} required>
              <option value="">Select a server…</option>
              {servers.data?.map((s) => <option key={s.id} value={s.id}>{s.hostName}</option>)}
            </Select>
          </Field>
          <Field label="Name"><Input value={name} onChange={(e) => setName(e.target.value)} placeholder="Finance" required /></Field>
          <Field label="Local path" hint="As seen on the server.">
            <Input value={localPath} onChange={(e) => setLocalPath(e.target.value)} placeholder="D:\Finance" className="font-mono" required />
          </Field>
          <Field label="UNC path" hint="As users reach it over the network.">
            <Input value={uncPath} onChange={(e) => setUncPath(e.target.value)} placeholder="\\fs01\Finance" className="font-mono" required />
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

export function SharesPage() {
  const shares = useShares();
  const servers = useServers();
  const [serverFilter, setServerFilter] = useState("");
  const host = new Map(servers.data?.map((s) => [s.id, s.hostName]));

  const columns: ColumnDef<Share>[] = [
    { header: "Name", cell: ({ row }) => <span className="font-medium">{row.original.name}</span> },
    { header: "Server", cell: ({ row }) => host.get(row.original.serverId) ?? "—" },
    { header: "Local path", cell: ({ row }) => <span className="font-mono text-xs">{row.original.localPath}</span> },
    { header: "UNC path", cell: ({ row }) => <span className="font-mono text-xs">{row.original.uncPath}</span> },
    { header: "Approval", cell: ({ row }) => (row.original.approved ? <Badge tone="success">Approved</Badge> : <Badge tone="neutral">Not approved</Badge>) },
  ];

  const rows = (shares.data ?? []).filter((s) => !serverFilter || s.serverId === serverFilter);

  return (
    <div className="flex flex-col gap-6">
      <Reveal>
        <PageHeader title="Shares" description="Shared folders on your servers, used to pick what a session watches." actions={<RegisterShareDrawer />} />
      </Reveal>
      <Reveal index={1} className="flex flex-col gap-4">
        <div className="max-w-xs">
          <Select value={serverFilter} onChange={(e) => setServerFilter(e.target.value)} aria-label="Filter by server">
            <option value="">All servers</option>
            {servers.data?.map((s) => <option key={s.id} value={s.id}>{s.hostName}</option>)}
          </Select>
        </div>
        {shares.isError ? (
          <p className="text-sm text-danger">{shares.error.message}</p>
        ) : (
          <DataTable
            columns={columns}
            data={rows}
            getRowId={(s) => s.id}
            loading={shares.isPending}
            empty={<EmptyState icon={FolderTree} title="No shares" description="Register a share so it can be added to a session in one click." />}
          />
        )}
      </Reveal>
    </div>
  );
}
