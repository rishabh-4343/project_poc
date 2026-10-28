using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Principal;

namespace Auditor.Agent.Pipeline;

public interface IAccountResolver
{
    string? Resolve(string sid);
}

public interface IProcessNameResolver
{
    string? Resolve(uint processId);
}

// SID -> DOMAIN\name through the OS, cached for an hour; an unresolvable SID is retried after a minute.
public sealed class AccountResolver : IAccountResolver
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(1);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, (string? Name, DateTimeOffset Expires)> _cache = new();

    public string? Resolve(string sid)
    {
        if (string.IsNullOrEmpty(sid))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (_cache.TryGetValue(sid, out var hit) && hit.Expires > now)
        {
            return hit.Name;
        }

        string? name = null;
        try
        {
            name = new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or ArgumentException or SystemException)
        {
            // Unknown or malformed SID: report no name rather than failing the event.
        }

        _cache[sid] = (name, now + (name is null ? NegativeTtl : Ttl));
        return name;
    }
}

// PID -> process name, cached by PID and start time so a recycled PID is not mistaken for the old process.
public sealed class ProcessNameResolver : IProcessNameResolver
{
    private readonly ConcurrentDictionary<uint, (DateTime Started, string Name)> _cache = new();

    public string? Resolve(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            var started = process.StartTime;
            if (_cache.TryGetValue(processId, out var hit) && hit.Started == started)
            {
                return hit.Name;
            }

            var name = process.ProcessName;
            _cache[processId] = (started, name);
            return name;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process has already exited, so the last name seen for this PID is the best answer.
            return _cache.TryGetValue(processId, out var last) ? last.Name : null;
        }
    }
}

// The server's shares (name -> local path), pushed by the backend through UpdateShares in A7. Empty until then.
public sealed class ShareMap
{
    private volatile IReadOnlyList<(string Name, string LocalPath)> _shares = [];

    public void Set(IEnumerable<(string Name, string LocalPath)> shares) =>
        _shares = shares.Select(s => (s.Name, s.LocalPath.TrimEnd('\\'))).OrderByDescending(s => s.Item2.Length).ToList();

    // The share whose local path is the longest prefix of `path`, or null.
    public string? ShareFor(string path) =>
        _shares.FirstOrDefault(s => path.Equals(s.LocalPath, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(s.LocalPath + "\\", StringComparison.OrdinalIgnoreCase)).Name;
}
