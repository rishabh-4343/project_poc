// Self-contained checks (no test framework package): run with `dotnet run --project Agent.Tests`. Exit code is 1 on any failure.
using Auditor.Agent.Control;
using Auditor.Agent.Driver;
using Auditor.Agent.Pipeline;
using Auditor.Agent.Outbox;
using Auditor.Agent.Storage;

var failures = 0;
SeqStore Seq(string d) => new(new LocalStore(d, new LocalCrypto(d)));
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name} {(ok ? "" : detail)}");
    if (!ok)
    {
        failures++;
    }
}

var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
var seq = 0L;
SequencedEvent Ev(AuditOperation op, string path, double sec, string? newPath = null, bool ok = true, string sid = "S-1") =>
    new(++seq, new RawEvent(1, op, AuditObjectType.File, ok ? AuditFlags.None : AuditFlags.Denied, 1, ok ? 0 : -1, t0.AddSeconds(sec), sid, path, newPath));
string Describe(IEnumerable<CorrelatedAction> a) => string.Join(" ", a.Select(x => $"{x.Seq}:{x.Event.Operation}{(x.MergedCount > 1 ? "x" + x.MergedCount : "")}"));

Console.WriteLine("== codec round trip");
var bytes = SimulatedDriverSource.Encode(AuditOperation.Renamed, AuditObjectType.File, AuditFlags.Denied, 77, -5, t0, "S-1-5-21-9", @"D:\a.txt", @"D:\b.txt");
var parsed = AuditRecordCodec.Parse(bytes);
Check("fields survive encode/parse", parsed is { Operation: AuditOperation.Renamed, ProcessId: 77, Status: -5, Sid: "S-1-5-21-9", Path: @"D:\a.txt", NewPath: @"D:\b.txt" } && !parsed.Success && parsed.Timestamp == t0);
Check("header is 32 bytes before the variable region", bytes.Length == 32 + 2 * ("S-1-5-21-9".Length + @"D:\a.txt".Length + @"D:\b.txt".Length));
var bad = (byte[])bytes.Clone();
bad[0] = 2;
Check("an unsupported version is rejected", Throws(() => AuditRecordCodec.Parse(bad)));
Check("a truncated record is rejected", Throws(() => AuditRecordCodec.Parse(bytes.AsSpan(0, 40))));

Console.WriteLine("== correlator: create, modify x3, rename, delete");
var c = new Correlator(TimeSpan.FromSeconds(5));
var all = new List<CorrelatedAction>();
all.AddRange(c.Process(Ev(AuditOperation.Created, @"D:\a.txt", 0)));
all.AddRange(c.Process(Ev(AuditOperation.Modified, @"D:\a.txt", 1)));
all.AddRange(c.Process(Ev(AuditOperation.Modified, @"D:\a.txt", 2)));
all.AddRange(c.Process(Ev(AuditOperation.Modified, @"D:\a.txt", 3)));
Check("modifies are held", Describe(all) == "1:Created");
Check("SafeSeq stays below the held seq", c.SafeSeq == 1, $"got {c.SafeSeq}");
all.AddRange(c.Process(Ev(AuditOperation.Renamed, @"D:\a.txt", 4, @"D:\b.txt")));
Check("a rename releases the merged modify first", Describe(all) == "1:Created 2:Modifiedx3 5:Renamed", Describe(all));
all.AddRange(c.Process(Ev(AuditOperation.Deleted, @"D:\b.txt", 5)));
all.AddRange(c.Process(Ev(AuditOperation.Deleted, @"D:\b.txt", 5.2)));
Check("disposition + cleanup is one Deleted", Describe(all).EndsWith("6:Deleted") && all.Count(a => a.Event.Operation == AuditOperation.Deleted) == 1, Describe(all));
Check("SafeSeq catches up when nothing is held", c.SafeSeq == 7, $"got {c.SafeSeq}");

Console.WriteLine("== correlator: windows and exceptions");
c = new Correlator(TimeSpan.FromSeconds(5));
seq = 0;
var o = new List<CorrelatedAction>();
o.AddRange(c.Process(Ev(AuditOperation.Modified, @"D:\a.txt", 0)));
o.AddRange(c.Process(Ev(AuditOperation.Modified, @"D:\a.txt", 20)));
Check("writes further apart than the window are separate actions", Describe(o) == "1:Modified", Describe(o));
o.AddRange(c.Flush(t0.AddSeconds(30)));
Check("a quiet file is released by Flush", Describe(o) == "1:Modified 2:Modified", Describe(o));
o.Clear();
o.AddRange(c.Process(Ev(AuditOperation.Modified, @"D:\a.txt", 40, sid: "S-1")));
o.AddRange(c.Process(Ev(AuditOperation.Modified, @"D:\a.txt", 41, sid: "S-2")));
o.AddRange(c.Process(Ev(AuditOperation.Modified, @"D:\a.txt", 42, ok: false)));
o.AddRange(c.FlushAll());
Check("different users and denied writes are not merged", o.Count == 3 && o.All(a => a.MergedCount == 1), Describe(o));
o.Clear();
o.AddRange(c.Process(Ev(AuditOperation.Deleted, @"D:\x.txt", 100)));
o.AddRange(c.Process(Ev(AuditOperation.Deleted, @"D:\x.txt", 200)));
Check("a delete long after the first is a new action", o.Count == 2, Describe(o));

Console.WriteLine("== enricher");
Check("path hash ignores case and a trailing backslash", PathHasher.Hash(@"D:\Finance\") == PathHasher.Hash(@"d:\FINANCE") && PathHasher.Hash(@"D:\a") != PathHasher.Hash(@"D:\b"));
Check("path hash is xxHash64 of the lowercase UTF-8 path", PathHasher.Hash("abc") == unchecked((long)System.IO.Hashing.XxHash64.HashToUInt64("abc"u8, 0)));
var shares = new ShareMap();
shares.Set([("Finance", @"D:\Finance"), ("Deep", @"D:\Finance\Deep")]);
Check("the longest share prefix wins", shares.ShareFor(@"d:\finance\deep\x.txt") == "Deep" && shares.ShareFor(@"D:\Finance\a.txt") == "Finance");
Check("a sibling folder with a shared name prefix is not in the share", shares.ShareFor(@"D:\FinanceOld\a.txt") is null && shares.ShareFor(@"E:\x") is null);
var opts = Microsoft.Extensions.Options.Options.Create(new Auditor.Agent.AgentOptions { TenantId = Guid.NewGuid(), ServerId = Guid.NewGuid() });
var enricher = new Enricher(opts, new FakeAccounts(), new FakeProcesses(), shares);
var enriched = enricher.Enrich(new CorrelatedAction(9, Ev(AuditOperation.Renamed, @"D:\Finance\a.txt", 0, @"D:\Finance\b.txt").Event with { ProcessId = 4242 }, 3));
Check("seq, tenant, server and times carry over", enriched.Seq == 9 && enriched.TenantId == opts.Value.TenantId && enriched.ServerId == opts.Value.ServerId && enriched.OccurredAt == t0);
Check("account, process, share and hash are filled", enriched is { ActorName: @"CORP\alice", ProcessName: "excel", ShareName: "Finance" } && enriched.PathHash == PathHasher.Hash(@"D:\Finance\a.txt"));
Check("rename keeps the new path, merged count goes to details", enriched.NewPath == @"D:\Finance\b.txt" && enriched.Details == "merged 3 writes" && enriched.Certainty == Certainty.Certain);
var realName = new AccountResolver().Resolve("S-1-5-18");
Check("the OS resolves a well-known SID", realName is not null && realName.EndsWith("SYSTEM", StringComparison.OrdinalIgnoreCase), realName ?? "null");
Check("an unknown SID resolves to no name", new AccountResolver().Resolve("S-1-5-21-1-2-3-99999") is null);
var json = System.Text.Json.JsonSerializer.Serialize(enriched, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
Check("JSON uses the backend field and enum names", json.Contains("\"operation\":\"Renamed\"") && json.Contains("\"objectType\":\"File\"") && json.Contains("\"certainty\":\"Certain\"") && json.Contains("\"pathHash\":"), json);

Console.WriteLine("== scope manager and router");
var applied = new List<DriverScope>();
var driverCtl = new RecordingDriver(applied);
var clock = new FakeClock(t0);
var scopesMgr = new ScopeManager(driverCtl, clock);
var router = new Router(scopesMgr);
AuditAction Act(AuditOperation op, string path) => enricher.Enrich(new CorrelatedAction(1, Ev(op, path, 0).Event with { Operation = op }, 1));
Check("with no scope nothing flows", !router.Accept(Act(AuditOperation.Created, @"D:\Finance\a.txt")));
var sA = Guid.NewGuid();
var sB = Guid.NewGuid();
Check("a scope without folders or operations is refused", !scopesMgr.Start(new StartScopeCommand(sA, Guid.NewGuid(), [AuditOperation.Created], false, [], null)) && applied.Count == 0);
scopesMgr.Start(new StartScopeCommand(sA, Guid.NewGuid(), [AuditOperation.Created, AuditOperation.Deleted], false, [new ScopeFolder(@"D:\Finance\", true, null)], null));
Check("starting a scope makes matching actions flow", router.Accept(Act(AuditOperation.Created, @"d:\finance\sub\a.txt")));
Check("operations outside the scope are dropped", !router.Accept(Act(AuditOperation.Modified, @"D:\Finance\a.txt")));
Check("paths outside the folder are dropped", !router.Accept(Act(AuditOperation.Created, @"D:\FinanceOld\a.txt")) && !router.Accept(Act(AuditOperation.Created, @"D:\Other\a.txt")));
Check("the driver was told the union", applied.Count == 1 && applied[0].Folders.Count == 1 && applied[0].Operations.SetEquals([AuditOperation.Created, AuditOperation.Deleted]));
Check("a repeated StartScope is a no-op", scopesMgr.Start(new StartScopeCommand(sA, Guid.NewGuid(), [AuditOperation.Created], false, [new ScopeFolder(@"D:\X", true, null)], null)) && applied.Count == 1 && scopesMgr.Count == 1);
scopesMgr.Start(new StartScopeCommand(sB, Guid.NewGuid(), [AuditOperation.Modified], false, [new ScopeFolder(@"D:\Finance", false, null)], null));
Check("a second scope widens the union", applied.Count == 2 && applied[1].Operations.Contains(AuditOperation.Modified) && router.Accept(Act(AuditOperation.Modified, @"D:\Finance\a.txt")));
Check("non-recursive means direct children only", !router.Accept(Act(AuditOperation.Modified, @"D:\Finance\sub\a.txt")));
Check("stopping a scope stops its actions only", scopesMgr.Stop(sB) && !router.Accept(Act(AuditOperation.Modified, @"D:\Finance\a.txt")) && router.Accept(Act(AuditOperation.Created, @"D:\Finance\a.txt")) && applied.Count == 3);
Check("stopping an unknown session changes nothing", !scopesMgr.Stop(Guid.NewGuid()) && applied.Count == 3);
scopesMgr.Stop(sA);
Check("stopping the last scope stops everything and clears the driver", !router.Accept(Act(AuditOperation.Created, @"D:\Finance\a.txt")) && applied[^1].Folders.Count == 0);
scopesMgr.Start(new StartScopeCommand(sA, Guid.NewGuid(), [AuditOperation.Created], false, [new ScopeFolder(@"D:\Finance", true, null)], t0.AddMinutes(10)));
Check("a scope is honoured before its MaxEndAt", router.Accept(Act(AuditOperation.Created, @"D:\Finance\a.txt")));
clock.Now = t0.AddMinutes(11);
Check("and ignored after it, even if the stop command is late", !router.Accept(Act(AuditOperation.Created, @"D:\Finance\a.txt")));

Console.WriteLine("== crypto, outbox, batcher, read merger");
var odir = Path.Combine(Path.GetTempPath(), "agent-ob-" + Guid.NewGuid().ToString("N"));
var ocrypto = new LocalCrypto(Path.Combine(odir, "state"));
var sealedBytes = ocrypto.Seal("secret path D-Finance"u8);
Check("sealed data is not readable plaintext", !System.Text.Encoding.UTF8.GetString(sealedBytes).Contains("Finance") && true);
Check("sealed data opens with the same key", System.Text.Encoding.UTF8.GetString(ocrypto.Open(sealedBytes)) == "secret path D-Finance");
Check("the key survives a restart", System.Text.Encoding.UTF8.GetString(new LocalCrypto(Path.Combine(odir, "state")).Open(sealedBytes)) == "secret path D-Finance");
var flipped = (byte[])sealedBytes.Clone();
flipped[^1] ^= 1;
Check("a tampered file is rejected", Throws2(() => ocrypto.Open(flipped)));

var outbox = new DiskOutbox(Path.Combine(odir, "outbox"), ocrypto, 10_000_000);
AuditAction Mk(long s, AuditOperation op = AuditOperation.Created, string path = @"D:\a.txt", string sid = "S-1", double sec = 0) =>
    enricher.Enrich(new CorrelatedAction(s, Ev(op, path, sec, sid: sid).Event, 1));
var fclock = new FakeClock(t0);
var batcher = new Batcher(outbox, 3, TimeSpan.FromSeconds(2), fclock);
for (var i = 1; i <= 7; i++)
{
    batcher.Add(Mk(i));
}

Check("a batch is cut at maxActions", outbox.Count == 2 && batcher.Pending == 1, $"count {outbox.Count}, pending {batcher.Pending}");
fclock.Now = t0.AddSeconds(3);
batcher.Tick();
Check("a quiet batch is cut after maxMs", outbox.Count == 3 && batcher.Pending == 0);
var first = outbox.PeekOldest()!.Value;
Check("the oldest batch comes first, with seq 1-3", first.Batch.Actions!.Select(a => a.Seq).SequenceEqual([1, 2, 3]));
Check("peeking twice gives the same batch id (a resend stays idempotent)", outbox.PeekOldest()!.Value.Batch.BatchId == first.Batch.BatchId);
Check("the file on disk does not contain the path in plain text", !File.ReadAllText(first.Path, System.Text.Encoding.Latin1).Contains("a.txt"));

var reopened = new DiskOutbox(Path.Combine(odir, "outbox"), ocrypto, 10_000_000);
Check("a restart finds every unacked batch", reopened.Count == 3 && reopened.PeekOldest()!.Value.Batch.BatchId == first.Batch.BatchId);
reopened.Delete(first.Path);
Check("an acknowledged batch is removed, the next is the oldest", reopened.Count == 2 && reopened.PeekOldest()!.Value.Batch.Actions![0].Seq == 4);
reopened.TryEnqueue(new OutboxBatch(OutboxBatch.ActionsKind, Guid.NewGuid(), [Mk(99)], null));
Check("new files continue the numbering after a restart", Path.GetFileName(new DiskOutbox(Path.Combine(odir, "outbox"), ocrypto, 10_000_000).PeekOldest()!.Value.Path) == "000000000002.batch");

var victim = Directory.GetFiles(Path.Combine(odir, "outbox"), "*.batch").Order().First();
var raw = File.ReadAllBytes(victim);
raw[^1] ^= 1;
File.WriteAllBytes(victim, raw);
var afterTamper = new DiskOutbox(Path.Combine(odir, "outbox"), ocrypto, 10_000_000).PeekOldest();
Check("a tampered outbox file is set aside and the next one is used", File.Exists(victim + ".corrupt") && afterTamper is not null && afterTamper.Value.Path != victim);

var tiny = new DiskOutbox(Path.Combine(odir, "tiny"), ocrypto, 1500);
var tinyBatcher = new Batcher(tiny, 1, TimeSpan.FromSeconds(1), fclock);
for (var i = 1; i <= 5; i++)
{
    tinyBatcher.Add(Mk(i));
}

Check("a full outbox refuses batches and counts the loss", tiny.Count >= 1 && tinyBatcher.Dropped >= 1 && tiny.Bytes <= 1500, $"count {tiny.Count} dropped {tinyBatcher.Dropped} bytes {tiny.Bytes}");

var rdir = new DiskOutbox(Path.Combine(odir, "reads"), ocrypto, 10_000_000);
var rb = new Batcher(rdir, 100, TimeSpan.FromSeconds(1), fclock);
rb.Add(Mk(1, AuditOperation.ContentView, @"D:\r.docx", "S-1", 0));
rb.Add(Mk(2, AuditOperation.Read, @"D:\r.docx", "S-1", 30));
rb.Add(Mk(3, AuditOperation.Read, @"D:\r.docx", "S-1", 90));
rb.Add(Mk(4, AuditOperation.Read, @"D:\r.docx", "S-2", 10));
rb.Add(Mk(5, AuditOperation.Read, @"D:\r.docx", "S-1", 3700));
rb.Add(Mk(6, AuditOperation.Created, @"D:\r.docx", "S-1", 5));
rb.Tick(force: true);
var rbatches = new List<OutboxBatch>();
while (rdir.PeekOldest() is { } next)
{
    rbatches.Add(next.Batch);
    rdir.Delete(next.Path);
}

var reads = rbatches.Where(b => b.Kind == OutboxBatch.ReadsKind).SelectMany(b => b.Reads!).ToList();
Check("reads leave the action batch", rbatches.Count(b => b.Kind == OutboxBatch.ActionsKind) == 1 && rbatches.First(b => b.Kind == OutboxBatch.ActionsKind).Actions!.Count == 1);
Check("reads merge per hour, user and path", reads.Count == 3 && reads.Single(r => r.ActorSid == "S-1" && r.BucketStart.Hour == 12).ReadCount == 3, string.Join("|", reads.Select(r => r.ActorSid + "@" + r.BucketStart.Hour + "x" + r.ReadCount)));
Check("a read row spans first to last read", reads.Single(r => r.ActorSid == "S-1" && r.BucketStart.Hour == 12) is var m && m.FirstAt == t0 && m.LastAt == t0.AddSeconds(90));
Check("the read batch has its own id", rbatches.Single(b => b.Kind == OutboxBatch.ReadsKind).BatchId != Guid.Empty);
Directory.Delete(odir, true);

Console.WriteLine("== command handler");
var cmdApplied = new List<DriverScope>();
var cmdScopes = new ScopeManager(new RecordingDriver(cmdApplied), fclock);
var handler = new CommandHandler(cmdScopes, fclock, Microsoft.Extensions.Logging.Abstractions.NullLogger<CommandHandler>.Instance);
var sess = Guid.NewGuid();
var startJson = System.Text.Json.JsonDocument.Parse($$"""{"sessionId":"{{sess}}","serverId":"{{Guid.NewGuid()}}","operations":["Created","Deleted"],"includeReads":false,"folders":[{"localPath":"D:\\Finance","recursive":true,"shareId":null}],"maxEndAt":null}""").RootElement;
var start = new AgentCommand(Guid.NewGuid(), "StartScope", fclock.Now.AddMinutes(2), startJson);
var r1 = handler.Handle(start);
Check("StartScope from the backend's JSON shape is applied", r1.Success && cmdScopes.Count == 1 && cmdApplied.Count == 1);
cmdScopes.Stop(sess);
var r2 = handler.Handle(start);
Check("a repeated command id repeats the answer and is not applied again", r2.Success && cmdScopes.Count == 0);
var stopJson = System.Text.Json.JsonDocument.Parse($$"""{"sessionId":"{{sess}}"}""").RootElement;
Check("StopScope for a scope that is not held still succeeds", handler.Handle(new AgentCommand(Guid.NewGuid(), "StopScope", fclock.Now.AddMinutes(2), stopJson)).Success);
Check("an expired command is refused", !handler.Handle(new AgentCommand(Guid.NewGuid(), "StartScope", fclock.Now.AddMinutes(-1), startJson)).Success);
Check("an unknown command type is refused", !handler.Handle(new AgentCommand(Guid.NewGuid(), "Explode", fclock.Now.AddMinutes(2), stopJson)).Success);
var emptyScope = System.Text.Json.JsonDocument.Parse("""{"sessionId":"00000000-0000-0000-0000-000000000009","operations":[],"folders":[]}""").RootElement;
Check("a scope with nothing to watch is refused", !handler.Handle(new AgentCommand(Guid.NewGuid(), "StartScope", fclock.Now.AddMinutes(2), emptyScope)).Success);

Console.WriteLine("== seq store");
var dir = Path.Combine(Path.GetTempPath(), "agent-seq-" + Guid.NewGuid().ToString("N"));
var s1 = Seq(dir);
var firstRun = Enumerable.Range(0, 5).Select(_ => s1.Next()).ToList();
Check("seq starts at 1 and increases", string.Join(",", firstRun) == "1,2,3,4,5");
var crashed = Seq(dir);
Check("after a crash seq never reuses a number", crashed.Next() > 5, "");
var dir2 = Path.Combine(Path.GetTempPath(), "agent-seq-" + Guid.NewGuid().ToString("N"));
var s2 = Seq(dir2);
for (var i = 0; i < 3; i++)
{
    s2.Next();
}

s2.Release();
Check("after a clean release seq continues without a gap", Seq(dir2).Next() == 4);
Directory.Delete(dir2, true);
Directory.Delete(dir, true);

// Optional: records built by the driver's C code (Backend/Driver/tests/run-core-tests.ps1 -Samples <file>),
// read back with the agent's frozen parser to prove the two sides agree byte for byte.
if (args.Length > 0 && File.Exists(args[0]))
{
    Console.WriteLine("== driver C code -> C# parser");
    var data = File.ReadAllBytes(args[0]);
    var parsedRecords = new List<RawEvent>();
    for (var at = 0; at + 4 <= data.Length;)
    {
        var len = BitConverter.ToInt32(data, at);
        parsedRecords.Add(AuditRecordCodec.Parse(data.AsSpan(at + 4, len)));
        at += 4 + len;
    }

    Check("three sample records were read", parsedRecords.Count == 3, $"got {parsedRecords.Count}");
    var rename = parsedRecords[0];
    Check("rename: operation, type, pid, sid, both paths",
        rename is { Operation: AuditOperation.Renamed, ObjectType: AuditObjectType.File, ProcessId: 4242, Sid: "S-1-5-21-9-8-7-1001" }
        && rename.Path == @"\Device\HarddiskVolume3\Finance\a.txt" && rename.NewPath == @"\Device\HarddiskVolume3\Finance\b.txt");
    Check("rename: timestamp is the FILETIME the C code wrote", rename.Timestamp == DateTimeOffset.FromFileTime(133800000000000000L));
    var denied = parsedRecords[1];
    Check("denied open of a folder", denied is { Operation: AuditOperation.Open, ObjectType: AuditObjectType.Folder, Status: unchecked((int)0xC0000022) } && !denied.Success && denied.NewPath is null);
    var perm = parsedRecords[2];
    Check("non-ASCII text survives as UTF-16LE", perm.Operation == AuditOperation.PermissionChange && perm.Path.EndsWith("café.txt", StringComparison.Ordinal) && perm.Sid == "S-1-5-32-544");
}

Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;

static bool Throws2(Action a)
{
    try
    {
        a();
        return false;
    }
    catch (System.Security.Cryptography.CryptographicException)
    {
        return true;
    }
}

static bool Throws(Action a)
{
    try
    {
        a();
        return false;
    }
    catch (InvalidDataException)
    {
        return true;
    }
}

sealed class FakeAccounts : IAccountResolver
{
    public string? Resolve(string sid) => @"CORP\alice";
}

sealed class FakeProcesses : IProcessNameResolver
{
    public string? Resolve(uint processId) => processId == 4242 ? "excel" : null;
}

sealed class RecordingDriver(List<DriverScope> applied) : IDriverControl
{
    public void ApplyScope(DriverScope scope) => applied.Add(scope);
}

sealed class FakeClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

static partial class Helpers
{
}
