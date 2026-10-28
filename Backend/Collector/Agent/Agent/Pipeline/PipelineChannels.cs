using System.Threading.Channels;
using Auditor.Agent.Driver;

namespace Auditor.Agent.Pipeline;

// Bounded channels between pipeline stages, so a slow stage pushes back on the one before it.
public sealed class PipelineChannels
{
    public Channel<SequencedEvent> Events { get; } =
        Channel.CreateBounded<SequencedEvent>(new BoundedChannelOptions(10_000) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    // Enriched actions that passed the router, waiting for the batcher.
    public Channel<AuditAction> Actions { get; } =
        Channel.CreateBounded<AuditAction>(new BoundedChannelOptions(10_000) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
}
