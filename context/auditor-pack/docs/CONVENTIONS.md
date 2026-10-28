# Auditor — Conventions (folder structure, naming, isolation)

These rules keep three developers in one repo without collisions. **They are mandatory.**
The one rule behind all of them: **you only ever create/edit files inside your own
module/feature folder; shared folders change only by agreement.**

## Repository tree (exact)
```
repo/
  docs/                              # these md files (source of truth)
  deploy/
    docker-compose.yml               # postgres + clickhouse
  backend/
    driver/                          # YOURS ONLY — C++ minifilter (WDK)
      AuditorDriver/                 # VS driver project
    agent/                           # YOURS ONLY — C# Windows service
      Auditor.Agent.sln
      src/Auditor.Agent/
    server/                          # SHARED — ASP.NET Core API (3 devs)
      Auditor.Server.sln
      src/
        Auditor.Server/              # web host (composition root)
          Program.cs                 # SHARED: registers each module (see "composition points")
          Common/                    # SHARED: DevAuth, Db base, ClickHouse, hubs, core access
          Modules/
            Wfsa/                     # YOUR module (Windows File Server Auditing)
              WfsaModule.cs           # DI + endpoint registration for this module
              Controllers/
              Services/
              Ingestion/
              Contracts/
            <Dev2Module>/             # another dev, same shape
            <Dev3Module>/
        Auditor.Contracts/           # SHARED: records used by server + agent (versioned carefully)
      db/                            # PostgreSQL migrations, one folder per module
        core/                        # SHARED (platform-owned, reviewed)
        wfsa/                        # YOURS
        <dev2>/  <dev3>/
      ch/                            # ClickHouse migrations, one folder per module
        wfsa/                        # YOURS
        <dev2>/  <dev3>/
  frontend/
    client/                          # SHARED — Next.js (3 devs)
      src/
        app/                         # SHARED routing shell; each module adds its own route segment folder
          wfsa/                      # YOUR routes (thin: import from features/)
        features/
          wfsa/                      # YOUR UI (pages, components, hooks, api) — all your work lives here
          <dev2>/  <dev3>/
        shared/                      # SHARED: api client, DevAuth stub, signalr client, ui kit, layout
```

## Naming rules
- **Module slug**: lowercase, no spaces. This module = `wfsa`. Used verbatim as the
  PostgreSQL schema name, ClickHouse database name, `Modules/Wfsa` folder (PascalCase of
  the slug), `features/wfsa` folder, route segment `/wfsa`, DI prefix, and command `module` field.
- **C# namespaces** follow folders: `Auditor.Server.Modules.Wfsa.Controllers`.
- **Controllers**: `<Thing>Controller.cs` inside `Modules/Wfsa/Controllers/`
  (e.g. `SessionsController.cs`, `ServersController.cs`). Route prefix
  `/api/wfsa/...`. Never put a controller outside your module folder.
- **Services**: `<Thing>Service.cs` in `Modules/Wfsa/Services/`.
- **Frontend**: components `PascalCase.tsx`, hooks `useThing.ts`, all under `features/wfsa/`.
  Routes in `app/wfsa/` are thin and import from `features/wfsa/`.
- **DB objects**: all of your tables live in schema `wfsa` (PG) / database `wfsa` (CH).
  Never create objects in `core` or another module's schema.

## Composition points (the ONLY shared files you touch, by agreement)
Adding a module touches exactly these shared files, each via a single added line/block:
1. `backend/server/src/Auditor.Server/Program.cs` — one line: `app.AddWfsaModule();`
2. `frontend/client/src/app/` — your own `wfsa/` route segment (no edit to others').
3. `frontend/client/src/shared/nav.ts` (or equivalent) — one entry for your module's nav link.
Coordinate edits to these three in chat/PR; everything else is inside your folder.

## Module isolation rules
- A module **reads** `core.*` (tenants, users, agents, servers, shares, commands) and may
  reference its ids. A module **never writes or alters** `core`.
- No cross-module imports, joins, or writes. If you need another module's data, call its
  API. (For the POC the modules don't need each other.)
- Each module registers itself in its own `*Module.cs`; the host just calls it.

## Migrations
- Tool: **DbUp** (C#) or **Flyway**. Each module has its own migration folder and its own
  tracking table: `core.schema_migrations`, `wfsa.schema_migrations`, etc.
- Order on a fresh DB: `core` first, then each module. Files numbered `0001_*.sql`, `0002_*.sql`.
- ClickHouse migrations run the same way from `ch/<module>/`.
- You run only your own folder; two devs migrating at once never touch the same objects.

## Git
- Branch naming: `wfsa/<short-topic>` (your module prefix). Keeps branches grouped by owner.
- One PR = one task from your `TASKS-*.md` where possible. PR title starts with `[wfsa]`.
- Never commit into another module's folder. Shared-file edits (the 3 composition points,
  `Common/`, `shared/`, `core/`, `Auditor.Contracts/`) require a reviewer from the team.
- Do not commit build output, `bin/`, `obj/`, `node_modules/`, driver `.sys`, secrets.

## Contracts between driver / agent / backend
- Driver↔agent record struct: defined once in a header shared by both (driver repo),
  mirrored in the agent. Version it with a leading byte.
- Agent↔backend DTOs: in `Auditor.Contracts` (shared). Add fields, never rename/reorder.
