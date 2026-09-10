using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using ModbusBridge.Core.Config;
using ModbusBridge.Core.Diagnostics;
using ModbusBridge.Core.Tags;

namespace ModbusBridge.Core.Inputs;

/// <summary>
/// Publishes host machine statistics as tags, so an HMI can show CPU/RAM/disk/network without a
/// game running. Everything here comes from the BCL or two kernel32 calls - no performance-counter
/// package, so the published exe keeps its no-third-party-dependency property.
/// </summary>
public sealed class PcStatsCollector : IAsyncDisposable
{
    public const string WriterId = "pcstats";

    private readonly PcStatsConfig _config;
    private readonly TagBus _bus;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    private readonly long _startedTicks = Clock.Ticks;

    // CPU is a rate, so it needs two samples of the system's cumulative time counters.
    private ulong _prevIdle, _prevKernel, _prevUser;
    private bool _haveCpuBaseline;

    // Same for the network counters.
    private long _prevRxBytes, _prevTxBytes, _prevNetTicks;
    private bool _haveNetBaseline;

    // Only constructed on Windows. Off it, the GPU comes from sysfs instead, and building a
    // PDH counter just to have it announce that PDH is unavailable is noise in the log.
    private readonly GpuCounter? _gpu = OperatingSystem.IsWindows() ? new GpuCounter() : null;

    // Enumerating every process is comparatively expensive; do it rarely.
    private long _lastProcessCountTicks;
    private double _lastProcessCount;

    public PcStatsCollector(PcStatsConfig config, TagBus bus)
    {
        _config = config;
        _bus = bus;
    }

    public bool IsRunning => _loop is not null;

    public void Start()
    {
        if (_loop is not null) return;

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));

        Log.Info("pcstats", $"Publishing host statistics under '{_config.TagPrefix}' " +
                            $"every {_config.IntervalMs} ms.");
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;

        _cts.Cancel();
        // Await the task before disposing the source it holds - disposing first throws on a
        // background thread and takes the process down.
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _cts.Dispose();
        _cts = null;
        _loop = null;
        _gpu?.Dispose();
        Log.Info("pcstats", "Stopped.");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { Sample(); }
            catch (Exception ex) { Log.Warn("pcstats", $"Sample failed: {ex.Message}"); }

            try { await Task.Delay(_config.IntervalMs, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Set(string name, double value) =>
        _bus.GetOrAdd(_config.TagPrefix + name).Set(TagValue.Good(value), WriterId);

    private void Sample()
    {
        Set("uptimeMinutes", Clock.MsSince(_startedTicks) / 60000.0);
        Set("logicalCpus", Environment.ProcessorCount);

        SampleCpu();
        SampleGpu();
        SampleMemory();
        SampleDisk();
        SampleNetwork();
        SampleProcessCount();
    }

    private void SampleCpu()
    {
        if (!OperatingSystem.IsWindows())
        {
            SampleCpuLinux();
            return;
        }
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return;

        var idleTicks = ToUInt64(idle);
        var kernelTicks = ToUInt64(kernel);
        var userTicks = ToUInt64(user);

        if (_haveCpuBaseline)
        {
            // Kernel time already includes idle, so total is kernel + user.
            var idleDelta = idleTicks - _prevIdle;
            var totalDelta = (kernelTicks - _prevKernel) + (userTicks - _prevUser);
            if (totalDelta > 0)
                Set("cpuPercent", Math.Clamp((1.0 - (double)idleDelta / totalDelta) * 100.0, 0, 100));
        }

        _prevIdle = idleTicks;
        _prevKernel = kernelTicks;
        _prevUser = userTicks;
        _haveCpuBaseline = true;
    }

    /// <summary>
    /// The /proc/stat counterpart. Deliberately reuses the same baseline fields as the Windows
    /// path: both are cumulative counters where only the delta between two samples means anything,
    /// so the first pass establishes a baseline and publishes nothing.
    /// </summary>
    private void SampleCpuLinux()
    {
        var now = LinuxHostStats.ReadCpuTimes();
        if (!now.IsValid) return;

        if (_haveCpuBaseline)
        {
            var idleDelta = now.Idle - _prevIdle;
            var totalDelta = now.Total - _prevKernel;
            if (totalDelta > 0)
                Set("cpuPercent", Math.Clamp((1.0 - (double)idleDelta / totalDelta) * 100.0, 0, 100));
        }

        _prevIdle = now.Idle;
        _prevKernel = now.Total;
        _haveCpuBaseline = true;
    }

    private void SampleGpu()
    {
        // Null while the counter is priming or unavailable - leave the tag untouched rather than
        // publishing a confident zero, which on a gauge is indistinguishable from an idle GPU.
        if (!OperatingSystem.IsWindows())
        {
            // AMD only - see LinuxHostStats.ReadGpuPercent. Null leaves the tag alone.
            if (LinuxHostStats.ReadGpuPercent() is { } linuxPercent) Set("gpuPercent", linuxPercent);
            return;
        }

        if (_gpu?.Sample() is { } percent) Set("gpuPercent", percent);
    }

    private void SampleMemory()
    {
        if (!OperatingSystem.IsWindows())
        {
            var linux = LinuxHostStats.ReadMemory();
            if (!linux.IsValid) return;
            Set("memTotalGb", linux.TotalGb);
            Set("memUsedGb", linux.UsedGb);
            Set("memPercent", linux.UsedPercent);
            return;
        }

        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status)) return;

        const double gib = 1024d * 1024d * 1024d;
        var total = status.ullTotalPhys / gib;
        var used = (status.ullTotalPhys - status.ullAvailPhys) / gib;

        Set("memTotalGb", total);
        Set("memUsedGb", used);
        Set("memPercent", status.dwMemoryLoad);
    }

    private void SampleDisk()
    {
        // Environment.SpecialFolder.System is empty off Windows, so this used to bail out
        // before touching a drive at all - disk stayed 0 on Linux for the same reason CPU did.
        var root = OperatingSystem.IsWindows()
            ? Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System))
            : "/";
        if (string.IsNullOrEmpty(root)) return;

        var drive = new DriveInfo(root);
        if (!drive.IsReady || drive.TotalSize <= 0) return;

        var used = drive.TotalSize - drive.TotalFreeSpace;
        Set("diskPercent", used * 100.0 / drive.TotalSize);
        Set("diskFreeGb", drive.TotalFreeSpace / (1024d * 1024d * 1024d));
    }

    private void SampleNetwork()
    {
        long rx = 0, tx = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            var stats = nic.GetIPStatistics();
            rx += stats.BytesReceived;
            tx += stats.BytesSent;
        }

        var now = Clock.Ticks;
        if (_haveNetBaseline)
        {
            var seconds = Clock.MsSince(_prevNetTicks) / 1000.0;
            if (seconds > 0)
            {
                // Counters are cumulative and can wrap or reset when an adapter drops; a negative
                // delta means "no useful sample" rather than a negative rate.
                var rxDelta = rx - _prevRxBytes;
                var txDelta = tx - _prevTxBytes;
                if (rxDelta >= 0) Set("netRxMbps", rxDelta * 8.0 / 1_000_000.0 / seconds);
                if (txDelta >= 0) Set("netTxMbps", txDelta * 8.0 / 1_000_000.0 / seconds);
            }
        }

        _prevRxBytes = rx;
        _prevTxBytes = tx;
        _prevNetTicks = now;
        _haveNetBaseline = true;
    }

    private void SampleProcessCount()
    {
        if (_lastProcessCountTicks != 0 && Clock.MsSince(_lastProcessCountTicks) < 5000)
        {
            Set("processCount", _lastProcessCount);
            return;
        }

        var processes = Process.GetProcesses();
        _lastProcessCount = processes.Length;
        foreach (var process in processes) process.Dispose();

        _lastProcessCountTicks = Clock.Ticks;
        Set("processCount", _lastProcessCount);
    }

    private static ulong ToUInt64(FILETIME value) =>
        ((ulong)(uint)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public int dwLowDateTime;
        public int dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
}
