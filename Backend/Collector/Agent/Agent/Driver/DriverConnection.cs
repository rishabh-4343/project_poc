using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Options;

namespace Auditor.Agent.Driver;

// Owns the single communication-port handle to the kernel driver. The driver's port allows exactly
// one connection (see AuditorPortTool.c), so the record reader (FilterGetMessage) and the scope
// control (FilterSendMessage) share one handle; a filter port carries send and receive independently,
// so a send from one thread while a get blocks on another is supported.
[SupportedOSPlatform("windows")]
public sealed class DriverConnection(IOptions<AgentOptions> options, ILogger<DriverConnection> logger) : IDisposable
{
    private readonly object _gate = new();
    private IntPtr _port = IntPtr.Zero;
    private bool _disposed;

    public string PortName => string.IsNullOrWhiteSpace(options.Value.DriverPortName)
        ? DriverProtocol.PortName : options.Value.DriverPortName;

    // Ensures the port is open and returns the handle. Throws Win32Exception if the driver is not
    // loaded / the port is not up. The returned handle may be closed by Drop/Dispose on another
    // thread; a native call on a closed handle fails cleanly (it does not crash), and the caller
    // treats that as a disconnect.
    public IntPtr Connect()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_port != IntPtr.Zero) return _port;
            int hr = FilterApi.FilterConnectCommunicationPort(PortName, 0, IntPtr.Zero, 0, IntPtr.Zero, out var port);
            if (hr != 0) throw FilterApi.Error(hr, $"FilterConnectCommunicationPort({PortName})");
            _port = port;
            logger.LogInformation("connected to driver port {Port}", PortName);
            return _port;
        }
    }

    // Sends one command and reads the fixed-size reply. Returns the number of reply bytes.
    public uint Send(byte[] command, byte[] reply)
    {
        var inBuf = Marshal.AllocHGlobal(command.Length);
        var outBuf = Marshal.AllocHGlobal(reply.Length);
        try
        {
            Marshal.Copy(command, 0, inBuf, command.Length);
            IntPtr port;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                port = _port != IntPtr.Zero ? _port : Connect();
            }
            int hr = FilterApi.FilterSendMessage(port, inBuf, (uint)command.Length, outBuf, (uint)reply.Length, out var returned);
            if (hr != 0) throw FilterApi.Error(hr, "FilterSendMessage");
            uint n = Math.Min(returned, (uint)reply.Length);
            Marshal.Copy(outBuf, reply, 0, (int)n);
            return n;
        }
        finally
        {
            Marshal.FreeHGlobal(inBuf);
            Marshal.FreeHGlobal(outBuf);
        }
    }

    // Closes the handle so a blocked FilterGetMessage returns and the reader can reconnect.
    public void Drop()
    {
        lock (_gate)
        {
            if (_port != IntPtr.Zero) { FilterApi.CloseHandle(_port); _port = IntPtr.Zero; }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            if (_port != IntPtr.Zero) { FilterApi.CloseHandle(_port); _port = IntPtr.Zero; }
        }
    }
}
