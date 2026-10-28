using System.Collections.Concurrent;

namespace server.Common.Hubs;

// Tracks which agents currently hold a hub connection, so commands are only pushed to reachable agents.
public sealed class AgentConnectionTracker
{
    private readonly ConcurrentDictionary<Guid, int> _connections = new();

    public bool IsOnline(Guid agentId) => _connections.TryGetValue(agentId, out var n) && n > 0;

    public void Connected(Guid agentId) => _connections.AddOrUpdate(agentId, 1, (_, n) => n + 1);

    public void Disconnected(Guid agentId) => _connections.AddOrUpdate(agentId, 0, (_, n) => Math.Max(0, n - 1));
}
