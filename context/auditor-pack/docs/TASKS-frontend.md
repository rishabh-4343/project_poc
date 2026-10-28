# TASKS — Frontend / client (shared Next.js)

## Context for a fresh agent
The frontend is shared by three developers. Task **F0 is the common foundation** (one
owner, first). Tasks **F1–F4 are the `wfsa` feature** (yours). Read `docs/00-OVERVIEW.md`
and `docs/CONVENTIONS.md`. All your work lives in `frontend/client/src/features/wfsa/`
and thin routes in `src/app/wfsa/`. Shared code is in `src/shared/`. Auth is a DevAuth
stub (fixed tenant/user); no login screen needed for the POC.

Stack: Next.js (App Router), TypeScript, Tailwind, TanStack Query, `@microsoft/signalr`.

---
### F0 · COMMON foundation  [assign ONE owner; do first]
- Goal: the app shell, API client, DevAuth stub, SignalR client, module nav pattern.
- Steps:
  1. Next.js project already exists at `frontend/client`. Add `src/shared/`:
     - `api.ts` — fetch wrapper; base URL from `NEXT_PUBLIC_API_URL`; attaches dev headers.
     - `auth.ts` — DevAuth stub: returns a fixed dev token/tenant; later swapped for real auth.
     - `signalr.ts` — builds a `/hubs/ui` connection with the dev token.
     - `ui/` — shared layout, nav, table, badge components.
     - `nav.ts` — module nav registry (array of {slug,label,href}); each module adds one entry (composition point).
  2. `src/app/layout.tsx` — QueryClientProvider, shared layout + nav.
  3. `.env.local` — `NEXT_PUBLIC_API_URL=http://localhost:5001`.
- Done when: `npm run dev` shows the shell with an (empty) module nav and calls `/health`.

### F1 · Wfsa feature skeleton + route
- Goal: the feature folder wired into a route and the nav.
- Steps:
  1. `src/features/wfsa/` with `index.ts`, `api.ts` (typed calls to `/api/wfsa/...`).
  2. `src/app/wfsa/page.tsx` — thin, renders `features/wfsa` dashboard.
  3. Add `{slug:'wfsa',label:'File Server Auditing',href:'/wfsa'}` to `shared/nav.ts` (composition point).
- Done when: the nav shows "File Server Auditing" and `/wfsa` renders a placeholder from the feature folder.

### F2 · Servers / shares / agents pages
- Goal: see agents online and register servers + shares.
- Steps:
  1. `features/wfsa/pages/AgentsPage.tsx` — agents list with live status.
  2. `ServersPage.tsx`, `SharesPage.tsx` — list + register forms (call `/api/wfsa` config endpoints).
- Done when: you can register a server + share from the UI and see them.

### F3 · Sessions list + new session
- Goal: start an audit session.
- Steps:
  1. `SessionsPage.tsx` — list with state badges.
  2. `NewSessionPage.tsx` — pick a server, add folders, tick operations (default set; Content View off with a "high volume" note). POST to `/api/wfsa/sessions`.
- Done when: starting a session routes to its live page and the backend receives it.

### F4 · Live session view + export
- Goal: watch actions arrive and export.
- Steps:
  1. `SessionPage.tsx` + `useSessionStream.ts` — initial page via REST, then `JoinSession` on `/hubs/ui`, merge ActionsAppended (dedupe by server+seq, cap ~2000).
  2. `ActionsTable.tsx` — columns: When, Server, Who, Object, Path, Operation (type+op), Allowed/Not, Process, From, + DDC. Tag Inferred ops.
  3. Stop button; CSV export via the backend endpoint (fetch with dev headers, save blob).
- Done when: create/modify/delete/rename in a watched folder appear live in the table; export downloads.
