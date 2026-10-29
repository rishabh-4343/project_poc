# Handoff — current state and what to do next

Written after a full read of the repo. Another assistant ("the architect") did the review;
you (Claude Code, local) do the hands-on run/fix/verify because you can build and run on Windows.

## Verdict: the runnable code is essentially COMPLETE, not mid-build
- Backend (Backend/Auditing/server): B0–B5 built, covered by tests/ PowerShell scripts. Solid.
- Agent (Backend/Collector/Agent): A1–A7 built against the SIMULATED driver. Solid.
- Frontend (Frontend/Lepide/client): F0–F4 built, polished, matches UI-SPEC and the backend
  contract. NOT yet verified in a browser against the live backend with real data.
- Driver (Backend/Driver): written in C++, NEVER built or tested. DO NOT touch it — it is
  tested tomorrow on a VM. Out of scope here.

## Your goal: make everything EXCEPT the driver run and demo-perfect, end to end.

## Hard rules
- Do NOT rewrite working code. Make minimal, targeted fixes only for things that actually
  break when you run it. This project's priority is zero new bugs.
- Do NOT touch Backend/Driver (the kernel driver) — VM testing is tomorrow.
- Do NOT commit anything unless the user asks.
- Module name is "wfsa" everywhere. Agent runs with DriverSource=Simulated.
- Stores: PostgreSQL (port 5433 here, a native Windows Postgres owns 5432) + ClickHouse (8123).

## Step 1 — bring the databases up (Docker)
    docker compose -f context/auditor-pack/deploy/docker-compose.yml up -d
Confirm both containers are healthy. Check Postgres is reachable on 5433 and ClickHouse on 8123.
(If the compose file maps 5432, note the backend expects 5433 — reconcile ports so the backend's
ConnectionStrings:Postgres matches the actual mapped port.)

## Step 2 — run the three parts (separate terminals)
Backend (prints the DEV AGENT KEY once on first start — save it):
    dotnet run --project Backend/Auditing/server/server/server.csproj --urls http://127.0.0.1:5058
Frontend:
    cd Frontend/Lepide/client; npm install; npm run dev
Agent (after you register+approve a server and have the dev agent key):
    dotnet run --project Backend/Collector/Agent/Agent/Agent.csproj
Set the agent's ApiKey/ServerId (env or appsettings) to the key and the registered server id.

## Step 3 — verify end to end in the browser (this is the real test)
Walk the full flow and confirm each works; fix what breaks:
1. Dashboard loads; metric cards + charts render (even if zero).
2. Register a server, approve it; register a share.
3. Start a session (pick folders + operations). It goes Requested -> Starting -> Active.
4. With the agent running (simulated driver), the live session table fills in real time,
   new rows animate in at the top with the accent flash. Filters work. "Shown" count updates.
5. Stop the session -> state Completed. Export CSV downloads real rows.
6. Agents page shows the agent online; dashboard "agents online" is correct.
7. Integrity events: trigger/observe at least the AgentOffline path (stop the agent).

## Step 4 — close these genuine gaps (only these)
1. Content-view ("File Content View") reads are stored in wfsa.read_actions but do NOT appear
   in the session report table or CSV. If feasible without large rework, surface reads in the
   session view (or confirm with the user whether to show them). Don't over-engineer.
2. Remove the stale "Placeholder host loop" comment / dead code in
   Backend/Collector/Agent/Agent/AgentWorker.cs if that file is unused.
3. Check the frontend against context/auditor-pack/docs/UI-SPEC.md: light+dark both correct,
   loading skeletons, empty states, hover/focus states, number count-ups on the dashboard.

## Step 5 — report back
Give the user a short punch-list: what worked, what you fixed, what still fails, and anything
that needs the architect's input. Keep working code intact.

## Known-acceptable (do NOT "fix" these)
DevAuth stub only; fixed ClickHouse TTL; report uses FINAL; copy-inference not built;
in-flight correlator events lost on hard crash. These are intended POC scope.
