using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Auditor.Agent.Driver;

// P/Invoke into fltlib.dll — the user-mode half of the minifilter communication port.
// These are the standard Filter Manager client APIs (the same ones the minispy/scanner WDK
// samples and our own tests/AuditorPortTool.c use). Windows-only; the agent targets net10.0-windows.
[SupportedOSPlatform("windows")]
internal static class FilterApi
{
    // FilterGetMessage prepends this header to every message the driver sends. ULONGLONG is
    // 8-byte aligned, so the struct is 16 bytes (4 + 4 pad + 8); the record/notice payload
    // begins at offset Marshal.SizeOf<FILTER_MESSAGE_HEADER>() — never hard-coded below.
    [StructLayout(LayoutKind.Sequential)]
    internal struct FILTER_MESSAGE_HEADER
    {
        public uint ReplyLength;
        public ulong MessageId;
    }

    [DllImport("fltlib.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int FilterConnectCommunicationPort(
        string lpPortName, uint dwOptions, IntPtr lpContext, ushort wSizeOfContext,
        IntPtr lpSecurityAttributes, out IntPtr hPort);

    [DllImport("fltlib.dll", ExactSpelling = true)]
    internal static extern int FilterSendMessage(
        IntPtr hPort, IntPtr lpInBuffer, uint dwInBufferSize,
        IntPtr lpOutBuffer, uint dwOutBufferSize, out uint lpBytesReturned);

    [DllImport("fltlib.dll", ExactSpelling = true)]
    internal static extern int FilterGetMessage(
        IntPtr hPort, IntPtr lpMessageBuffer, uint dwMessageBufferSize, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);

    internal static readonly int MessageHeaderSize = Marshal.SizeOf<FILTER_MESSAGE_HEADER>();

    internal static Win32Exception Error(int hr, string what) => new(hr, $"{what} failed: 0x{hr:X8}");
}
