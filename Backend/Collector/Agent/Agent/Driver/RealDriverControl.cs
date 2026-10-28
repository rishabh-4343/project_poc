using System.Runtime.Versioning;
using Auditor.Agent.Pipeline;

namespace Auditor.Agent.Driver;

// Real driver control: encodes the union scope as a SET_SCOPE command and sends it over the port.
// Shares the one DriverConnection with RealDriverSource. ApplyScope never throws into the hub
// command handler — the backend resends StartScope for every running session on reconnect.
[SupportedOSPlatform("windows")]
public sealed class RealDriverControl(DriverConnection connection, NtPathMap paths, ILogger<RealDriverControl> logger) : IDriverControl
{
    public void ApplyScope(DriverScope scope)
    {
        try
        {
            var command = DriverProtocol.EncodeSetScope(scope, paths);
            var reply = new byte[DriverProtocol.ReplySize];
            uint n = connection.Send(command, reply);
            var r = DriverProtocol.ParseReply(reply, (int)n);

            if (r.FolderCount != (uint)scope.Folders.Count)
                logger.LogWarning("driver accepted {Accepted} of {Requested} scope folder(s) (unmapped volumes are skipped)",
                    r.FolderCount, scope.Folders.Count);

            logger.LogInformation(
                "driver scope applied: {Folders} folder(s), operations [{Operations}], mask 0x{Mask:X8}; driver totals sent={Sent} dropped={Dropped}",
                r.FolderCount, string.Join(",", scope.Operations.OrderBy(o => (byte)o)), r.OperationMask, r.SentTotal, r.DroppedTotal);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "failed to apply driver scope ({Folders} folder(s))", scope.Folders.Count);
        }
    }
}
