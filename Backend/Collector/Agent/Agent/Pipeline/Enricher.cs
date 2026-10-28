using Auditor.Agent.Driver;
using Microsoft.Extensions.Options;

namespace Auditor.Agent.Pipeline;

// Adds account name, process name, share and path_hash. Path stays the local path: the backend matches session scopes against it.
public sealed class Enricher(
    IOptions<AgentOptions> options, IAccountResolver accounts, IProcessNameResolver processes, ShareMap shares)
{
    public AuditAction Enrich(CorrelatedAction action)
    {
        var e = action.Event;
        var o = options.Value;

        return new AuditAction(
            o.TenantId, o.ServerId, action.Seq, e.Timestamp.ToUniversalTime(),
            e.ObjectType, e.Operation, e.Success,
            (e.Flags & AuditFlags.Inferred) != 0 ? Certainty.Inferred : Certainty.Certain,
            e.Path, PathHasher.Hash(e.Path), e.NewPath, shares.ShareFor(e.Path),
            e.Sid, accounts.Resolve(e.Sid), null, processes.Resolve(e.ProcessId),
            null, null, action.MergedCount > 1 ? $"merged {action.MergedCount} writes" : null);
    }
}
