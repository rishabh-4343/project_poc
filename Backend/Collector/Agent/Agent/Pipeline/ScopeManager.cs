using Auditor.Agent.Driver;

namespace Auditor.Agent.Pipeline;

public sealed record ScopeFolder(string LocalPath, bool Recursive, Guid? ShareId);

// The StartScope command payload sent by the backend (operations are the shared operation names).
public sealed record StartScopeCommand(
    Guid SessionId, Guid ServerId, List<AuditOperation> Operations, bool IncludeReads, List<ScopeFolder> Folders, DateTimeOffset? MaxEndAt);

// The union of all active scopes, which is what the driver is told to watch.
public sealed record DriverScope(IReadOnlyList<ScopeFolder> Folders, IReadOnlySet<AuditOperation> Operations);

// Tells the driver what to watch. The real implementation sends the D2 message over the driver port.
public interface IDriverControl
{
    void ApplyScope(DriverScope scope);
}

public sealed class LoggingDriverControl(ILogger<LoggingDriverControl> logger) : IDriverControl
{
    public void ApplyScope(DriverScope scope) =>
        logger.LogInformation(
            "driver scope: {Folders} folder(s), operations [{Operations}]",
            scope.Folders.Count, string.Join(",", scope.Operations.OrderBy(o => (byte)o)));
}

// Holds the active session scopes. They live in memory only: the backend resends StartScope for every running
// session whenever the agent reconnects.
public sealed class ScopeManager(IDriverControl driver, TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, StartScopeCommand> _scopes = new();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _scopes.Count;
            }
        }
    }

    public IReadOnlyList<Guid> ActiveSessionIds
    {
        get
        {
            lock (_gate)
            {
                return _scopes.Keys.ToList();
            }
        }
    }

    // Returns true when the scope is now active, including when the same session was already held (a no-op).
    public bool Start(StartScopeCommand scope)
    {
        if (scope.Folders is not { Count: > 0 } || scope.Operations is not { Count: > 0 })
        {
            return false;
        }

        lock (_gate)
        {
            if (_scopes.ContainsKey(scope.SessionId))
            {
                return true;
            }

            _scopes[scope.SessionId] = scope;
            Push();
            return true;
        }
    }

    public bool Stop(Guid sessionId)
    {
        lock (_gate)
        {
            var removed = _scopes.Remove(sessionId);
            if (removed)
            {
                Push();
            }

            return removed;
        }
    }

    // True when any active scope contains the action; this is the Router's test.
    public bool Matches(AuditAction a)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            return _scopes.Values.Any(s => (s.MaxEndAt is null || s.MaxEndAt > now) && s.Operations.Contains(a.Operation) && InFolders(s, a.Path));
        }
    }

    // Same path rule as the backend's SessionScope: case-insensitive; non-recursive means direct children only.
    private static bool InFolders(StartScopeCommand scope, string path)
    {
        foreach (var folder in scope.Folders)
        {
            var root = folder.LocalPath.Trim().TrimEnd('\\');
            if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)
                && (folder.Recursive || path.IndexOf('\\', root.Length + 1) < 0))
            {
                return true;
            }
        }

        return false;
    }

    private void Push()
    {
        var folders = _scopes.Values.SelectMany(s => s.Folders)
            .GroupBy(f => (f.LocalPath.Trim().TrimEnd('\\').ToLowerInvariant(), f.Recursive))
            .Select(g => g.First()).ToList();
        var operations = _scopes.Values.SelectMany(s => s.Operations).ToHashSet();
        driver.ApplyScope(new DriverScope(folders, operations));
    }
}

// Keeps only actions inside an active scope, before batching.
public sealed class Router(ScopeManager scopes)
{
    public bool Accept(AuditAction action) => scopes.Matches(action);
}
