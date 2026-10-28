# Auditor — Frontend (React / Next.js)

Calls the C# API with a JWT; listens on `/hubs/ui` for live updates. Mirrors the
earlier frontend guide; only the data shape changed (driver -> same action schema).

## Stack
Next.js (App Router) + TypeScript, Tailwind, TanStack Query, `@microsoft/signalr`,
`openapi-typescript` to generate types from the backend's OpenAPI.

## Pages
```
login
dashboard            agents online, active sessions, counts (from daily rollup)
agents               add agent (show key once), status live
servers / [id]       registered servers, shares
sessions             list
sessions/new         pick server + folders + operations, start
sessions/[id]        live action table, stop, export
```

## Live session view
```ts
const conn = new HubConnectionBuilder()
  .withUrl(`${API}/hubs/ui`, { accessTokenFactory: () => getToken() ?? '' })
  .withAutomaticReconnect().build();
conn.on('ActionsAppended', (sid, actions) => { if (sid===id) merge(actions); });
conn.start().then(() => conn.invoke('JoinSession', id));
```
Merge newest-first, dedupe by (serverId, seq), cap ~2000 rows in memory; older pages via
keyset endpoint.

## Report table columns
When | Server | Who | Object (name) | Path | Operation (Type + Op) | Allowed/Not |
Process | From | + DDC (content type, risk, tags, value). Mark Inferred ops (copy,
rename destination) with a tag.

## New-session form
Choose a server, add one or more folders, tick operations (default: Created, Modified,
Deleted, Renamed, Permission/Owner change, Open-Denied; Content View off by default with
a "high volume" note). Submit -> POST /api/sessions -> route to the session page.

## POC
Login, agents list, start/stop a session on a server+folder, live table, CSV export.
