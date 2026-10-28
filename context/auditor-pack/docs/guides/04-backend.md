# Auditor — Backend API (C# / ASP.NET Core)

Authenticates agents and users, runs sessions, ingests facts to ClickHouse, serves
reports. PostgreSQL for config, ClickHouse for facts.

## Project & packages
- ASP.NET Core (.NET 10), minimal APIs.
- `Npgsql` + EF Core (`Npgsql.EntityFrameworkCore.PostgreSQL`) for config tables.
- `ClickHouse.Client` (ADO.NET) for facts; bulk insert via `ClickHouseBulkCopy`.
- `Microsoft.AspNetCore.SignalR` for agent + UI channels.
- `Microsoft.AspNetCore.Authentication.JwtBearer`.

## Layout
```
Auditor.Backend/
  Program.cs
  Auth/        AgentKeyAuthHandler, TokenService, ClaimsExtensions
  Hubs/        AgentHub (/hubs/agent), UiHub (/hubs/ui)
  Data/Pg/     EF Core DbContext for core.* + audit.* config
  Data/Ch/     ClickHouseWriter (bulk insert), ClickHouseReader (reports)
  Services/    SessionService, CommandService, IngestionService, Notifier
  Api/         Auth/Agents/Servers/Shares/Sessions endpoints
  Workers/     CommandExpiryWorker
```

## Two connections
```csharp
builder.Services.AddDbContext<ConfigDb>(o => o.UseNpgsql(pgConn));     // core + audit config
builder.Services.AddSingleton(_ => new ClickHouseConnectionFactory(chConn)); // facts
```

## Agent hub (ingest)
```csharp
[Authorize(AuthenticationSchemes = AgentKeyAuthHandler.Scheme)]
public sealed class AgentHub(IngestionService ingest, SessionService sessions, AgentStatus status) : Hub
{
  public override async Task OnConnectedAsync() {
    var (tenant, agent) = Context.User!.Agent();
    await Groups.AddToGroupAsync(Context.ConnectionId, $"agent:{agent}");
    await status.OnlineAsync(tenant, agent);
    await sessions.ResendActiveScopesAsync(agent);           // re-sync after reconnect
  }
  public Task<BatchAck> EventBatch(ActionBatch b)  => ingest.IngestActionsAsync(Context.User!.Agent(), b);
  public Task<BatchAck> ReadBatch(ReadBatch b)     => ingest.IngestReadsAsync(Context.User!.Agent(), b);
  public Task Heartbeat(Heartbeat h)               => status.HeartbeatAsync(Context.User!.Agent(), h);
  public Task IntegrityEvent(IntegrityEvent e)     => ingest.RecordIntegrityAsync(Context.User!.Agent(), e);
}
```
Raise SignalR message size for this hub: `MaximumReceiveMessageSize = 8*1024*1024`.

## Ingestion to ClickHouse (the hot path)
1. Verify the server belongs to the agent's tenant (cached).
2. Stamp DDC fields: look up `ddc.file_classification` by (tenant, server, path_hash)
   in PostgreSQL (or a cached snapshot) and copy content_type/risk/tags/value onto each row.
3. **Bulk insert** into `audit.actions` with `ClickHouseBulkCopy` (columnar, async).
4. Update PostgreSQL `audit.agent_positions.last_seq = greatest(...)` and session counts.
5. Ack the batch; push new rows to the UI hub group `session:{id}`.

```csharp
using var bulk = new ClickHouseBulkCopy(conn) {
    DestinationTableName = "audit.actions", BatchSize = b.Actions.Count };
await bulk.InitAsync();
await bulk.WriteToServerAsync(b.Actions.Select(ToRow));   // IEnumerable<object[]>
```
Dedup is structural: ReplacingMergeTree on (tenant,server,occurred_at,seq) collapses a
resent batch on merge, and reports use `FINAL` or a `argMax`/`GROUP BY` where exactness matters.

## Sessions = driver scopes
Start: validate user role + share; create `audit.sessions` (Starting) +
`audit.session_folders`; issue `StartScope` command to the agent (folders + operation
mask); on CommandResult -> Active. Stop: issue `StopScope`; on result -> Completed. Reads
in the scope just add the content-view operation; no SACL needed (the driver sees reads).

## Reports (read path)
- Session report page: query `audit.actions` filtered by tenant+server+time window+path
  prefix+operations, `ORDER BY occurred_at DESC LIMIT 200`, keyset on (occurred_at, seq).
- Dashboards: read `audit.daily_action_counts` (the MV rollup), tiny and fast.
- Report columns come straight from the row (When, Server, Who, Object, Operation,
  Allowed/Not, Process, From) + DDC fields already on the row.

## UI channel & auth
- Users: JWT (POC) / OIDC (prod); tenant + role claims; every query filtered by tenant.
- UiHub groups: `tenant:{id}` and `session:{id}`; Notifier pushes ActionsAppended,
  SessionUpdated, AgentUpdated.

## POC
One tenant seeded; agent key auth; ingest to ClickHouse; sessions start/stop via the
agent; live session view. Skip OIDC, cells, online connectors.
