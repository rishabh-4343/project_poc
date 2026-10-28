# Auditor (wfsa: Windows File Server Auditing)

Backend (ASP.NET Core) + agent (Worker Service) + frontend (Next.js), with PostgreSQL and ClickHouse in Docker. The kernel driver is not built yet; the agent uses a simulated driver.

```powershell
pwsh tools/dev-up.ps1      # databases, backend, a paired agent, frontend  -> http://localhost:3000/wfsa
pwsh tools/dev-down.ps1    # stop them (the database containers keep running)
```

Tests (they reset the local dev databases, so each needs `-Yes`):

```powershell
pwsh Backend/Auditing/server/tests/run-all.ps1 -Yes
dotnet run --project Backend/Collector/Agent/Agent.Tests
pwsh Backend/Collector/Agent/tests/e2e.ps1 -Yes
```

Details: `Backend/Auditing/server/NOTES.md`, `Backend/Collector/Agent/NOTES.md`; the design is in `context/auditor-pack/docs/`.
