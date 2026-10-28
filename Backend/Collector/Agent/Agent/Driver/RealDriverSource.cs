using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace Auditor.Agent.Driver;

// Real kernel-driver record source. Connects to the minifilter port and reads messages with
// FilterGetMessage on a dedicated blocking thread, tells a record from the overflow notice by the
// first payload byte, rewrites NT paths to drive letters, and hands frozen-format record bytes to
// the pipeline exactly as SimulatedDriverSource would — so everything downstream is unchanged.
// The native blocking read runs on its own thread and feeds a channel that ReadAsync drains.
[SupportedOSPlatform("windows")]
public sealed class RealDriverSource(DriverConnection connection, NtPathMap paths, ILogger<RealDriverSource> logger) : IDriverSource
{
    // The driver caps a single record body at 70 KB (tests/AuditorPortTool.c MSG_BODY_BYTES); this matches.
    private const int MaxPayload = 70 * 1024;

    public async IAsyncEnumerable<DriverSignal> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<DriverSignal>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        await using var reg = ct.Register(connection.Drop); // unblock a waiting FilterGetMessage on shutdown

        var thread = new Thread(() => ReadLoop(channel.Writer, ct)) { IsBackground = true, Name = "driver-native-reader" };
        thread.Start();

        try
        {
            await foreach (var signal in channel.Reader.ReadAllAsync(ct))
                yield return signal;
        }
        finally
        {
            connection.Drop();
        }
    }

    private void ReadLoop(ChannelWriter<DriverSignal> writer, CancellationToken ct)
    {
        int header = FilterApi.MessageHeaderSize;
        IntPtr buf = Marshal.AllocHGlobal(header + MaxPayload);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                IntPtr port;
                try { port = connection.Connect(); }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested) break;
                    logger.LogWarning("driver not reachable ({Reason}); retrying in 2s", ex.Message);
                    if (ct.WaitHandle.WaitOne(2000)) break;
                    continue;
                }

                int hr = FilterApi.FilterGetMessage(port, buf, (uint)(header + MaxPayload), IntPtr.Zero);
                if (hr != 0)
                {
                    if (ct.IsCancellationRequested) break;
                    logger.LogWarning("FilterGetMessage failed (0x{Hr:X8}); reconnecting", hr);
                    connection.Drop();
                    if (ct.WaitHandle.WaitOne(1000)) break;
                    continue;
                }

                byte marker = Marshal.ReadByte(buf, header); // first payload byte
                if (marker == DriverProtocol.NoticeMarker)
                {
                    var notice = new byte[DriverProtocol.OverflowNoticeSize];
                    Marshal.Copy(buf + header, notice, 0, notice.Length);
                    writer.TryWrite(new DriverSignal.Overflow(DriverProtocol.ParseOverflowDropped(notice)));
                    continue;
                }

                // A record: its own header carries the variable-region lengths (sidLen@24, pathLen@26, newPathLen@28).
                int sidLen  = Marshal.ReadInt16(buf, header + 24) & 0xFFFF;
                int pathLen = Marshal.ReadInt16(buf, header + 26) & 0xFFFF;
                int newLen  = Marshal.ReadInt16(buf, header + 28) & 0xFFFF;
                int total   = AuditRecordCodec.HeaderSize + sidLen + pathLen + newLen;
                if (total > MaxPayload)
                {
                    logger.LogError("driver record too large ({Total} bytes); dropped", total);
                    continue;
                }

                var rec = new byte[total];
                Marshal.Copy(buf + header, rec, 0, total);
                writer.TryWrite(new DriverSignal.Record(TranslatePaths(rec)));
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "driver read loop crashed");
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
            writer.TryComplete();
        }
    }

    // Re-emit the record with NT paths rewritten to drive letters, every other byte identical. If the
    // volume is unmapped, ToDos returns the same string instance and we send the record untouched.
    private byte[] TranslatePaths(byte[] rec)
    {
        try
        {
            var e = AuditRecordCodec.Parse(rec);
            var path = paths.ToDos(e.Path);
            var newPath = e.NewPath is null ? null : paths.ToDos(e.NewPath);
            if (ReferenceEquals(path, e.Path) && ReferenceEquals(newPath, e.NewPath)) return rec;
            return SimulatedDriverSource.Encode(e.Operation, e.ObjectType, e.Flags, e.ProcessId, e.Status, e.Timestamp, e.Sid, path, newPath);
        }
        catch (InvalidDataException)
        {
            return rec; // let DriverReader.Parse log the version/format rejection
        }
    }
}
