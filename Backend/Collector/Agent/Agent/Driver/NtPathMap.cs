using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Auditor.Agent.Driver;

// Translates between drive-letter paths (D:\Finance\a.txt) and NT device paths
// (\Device\HarddiskVolume4\Finance\a.txt). The kernel driver speaks NT paths only (it does no
// per-volume translation); the rest of the agent and the backend speak drive-letter paths.
// AuditorPortTool.c does the same translation in user mode. Local fixed/removable volumes only —
// the driver never attaches to network or RAW volumes, so UNC paths never appear here.
[SupportedOSPlatform("windows")]
public sealed class NtPathMap(ILogger<NtPathMap> logger)
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDeviceW(string lpDeviceName, [Out] char[] lpTargetPath, uint ucchMax);

    private readonly object _gate = new();
    private Dictionary<string, string> _driveToDevice = new(StringComparer.OrdinalIgnoreCase); // "C:" -> "\Device\HarddiskVolume3"
    private List<(string device, string drive)> _deviceToDrive = new();                        // longest device first
    private bool _loaded;

    private void Load(bool force)
    {
        lock (_gate)
        {
            if (_loaded && !force) return;
            var d2v = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var v2d = new List<(string, string)>();
            var buf = new char[1024];
            for (char c = 'A'; c <= 'Z'; c++)
            {
                var drive = $"{c}:";
                uint n = QueryDosDeviceW(drive, buf, (uint)buf.Length);
                if (n == 0) continue;                         // no such drive
                int end = Array.IndexOf(buf, '\0');            // first of the double-null-terminated list
                if (end <= 0) continue;
                var device = new string(buf, 0, end);
                if (!device.StartsWith('\\')) continue;
                d2v[drive] = device;
                v2d.Add((device, drive));
            }
            v2d.Sort((x, y) => y.Item1.Length.CompareTo(x.Item1.Length)); // HarddiskVolume10 before HarddiskVolume1
            _driveToDevice = d2v;
            _deviceToDrive = v2d;
            _loaded = true;
        }
    }

    // D:\Finance -> \Device\HarddiskVolumeN\Finance ; null if the drive letter is unknown.
    public string? ToNt(string dosPath)
    {
        if (string.IsNullOrWhiteSpace(dosPath)) return null;
        var p = dosPath.Trim();
        if (p.Length < 2 || p[1] != ':') return null;
        var drive = p[..2];

        var device = Lookup(drive);
        if (device is null) { Load(force: true); device = Lookup(drive); }
        if (device is null) { logger.LogWarning("no NT device for drive {Drive}; folder skipped", drive); return null; }

        var rest = p[2..];
        if (rest.Length == 0) return device;
        return rest[0] == '\\' ? device + rest : device + "\\" + rest;
    }

    // \Device\HarddiskVolumeN\Finance\a.txt -> D:\Finance\a.txt ; the input unchanged if unmapped.
    public string ToDos(string ntPath)
    {
        if (string.IsNullOrEmpty(ntPath) || ntPath[0] != '\\') return ntPath;
        Load(force: false);
        var hit = Match(ntPath);
        if (hit is null) { Load(force: true); hit = Match(ntPath); }
        if (hit is not null) return hit;
        logger.LogDebug("no drive letter for NT path {Path}; passing it through", ntPath);
        return ntPath;
    }

    private string? Lookup(string drive)
    {
        Load(force: false);
        lock (_gate) { return _driveToDevice.TryGetValue(drive, out var d) ? d : null; }
    }

    private string? Match(string ntPath)
    {
        lock (_gate)
        {
            foreach (var (device, drive) in _deviceToDrive)
            {
                if (ntPath.Equals(device, StringComparison.OrdinalIgnoreCase)) return drive;
                if (ntPath.Length > device.Length
                    && ntPath.AsSpan(0, device.Length).Equals(device, StringComparison.OrdinalIgnoreCase)
                    && ntPath[device.Length] == '\\')
                    return drive + ntPath[device.Length..];
            }
        }
        return null;
    }
}
