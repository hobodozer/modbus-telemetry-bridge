using System.Runtime.InteropServices;
using ModbusBridge.Core.Diagnostics;

namespace ModbusBridge.Core.Inputs;

/// <summary>
/// GPU utilisation from the Windows "GPU Engine" performance counters, read through PDH.
///
/// PDH lives in pdh.dll, which ships with Windows, so this keeps the published executable free of
/// third-party packages - System.Diagnostics.PerformanceCounter would be a NuGet reference.
///
/// Windows reports one counter instance per process per engine, so what Task Manager calls "3D"
/// is the sum of every instance whose name ends in engtype_3D.
/// </summary>
internal sealed class GpuCounter : IDisposable
{
    private const string CounterPath = @"\GPU Engine(*)\Utilization Percentage";

    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhCstatusValidData = 0x00000000;
    private const uint PdhCstatusNewData = 0x00000001;

    private IntPtr _query;
    private IntPtr _counter;
    private bool _primed;
    private bool _warned;

    public bool IsAvailable => _query != IntPtr.Zero;

    public GpuCounter()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0)
        {
            _query = IntPtr.Zero;
            return;
        }

        // The English variant is required: counter names are localised, and the literal path
        // above only matches on an English install otherwise.
        if (PdhAddEnglishCounter(_query, CounterPath, IntPtr.Zero, out _counter) != 0)
        {
            Log.Info("pcstats", "No GPU Engine performance counters on this machine; GPU stays 0.");
            Dispose();
        }
    }

    /// <summary>
    /// Total 3D engine utilisation as a percentage, or null until a second sample exists -
    /// these are rate counters and the first collection only establishes a baseline.
    /// </summary>
    public double? Sample()
    {
        if (_query == IntPtr.Zero) return null;

        if (PdhCollectQueryData(_query) != 0) return null;
        if (!_primed) { _primed = true; return null; }

        uint size = 0, count = 0;
        var status = PdhGetFormattedCounterArray(_counter, PdhFmtDouble, ref size, out count, IntPtr.Zero);
        if (status != PdhMoreData || size == 0) return null;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(_counter, PdhFmtDouble, ref size, out count, buffer) != 0)
                return null;

            var itemSize = Marshal.SizeOf<PdhFmtCounterValueItem>();
            double total = 0;

            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(buffer + i * itemSize);
                if (item.Value.CStatus != PdhCstatusValidData && item.Value.CStatus != PdhCstatusNewData)
                    continue;

                var name = Marshal.PtrToStringUni(item.Name);
                if (name is null || !name.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase))
                    continue;

                total += item.Value.DoubleValue;
            }

            // Summing per-process instances can exceed 100 on a multi-engine adapter.
            return Math.Clamp(total, 0, 100);
        }
        catch (Exception ex)
        {
            if (!_warned)
            {
                _warned = true;
                Log.Warn("pcstats", $"GPU counter read failed, leaving GPU at 0: {ex.Message}");
            }
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (_query == IntPtr.Zero) return;
        PdhCloseQuery(_query);
        _query = IntPtr.Zero;
        _counter = IntPtr.Zero;
    }

    // PDH_FMT_COUNTERVALUE is a DWORD followed by an 8-byte union, so on x64 there are four
    // bytes of padding between them. Getting this wrong reads the wrong half of the value.
    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValue
    {
        public uint CStatus;
        public uint Padding;
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItem
    {
        public IntPtr Name;
        public PdhFmtCounterValue Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData,
                                                    out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format,
                                                           ref uint bufferSize, out uint itemCount,
                                                           IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
