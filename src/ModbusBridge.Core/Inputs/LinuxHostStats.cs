using System.Globalization;

namespace ModbusBridge.Core.Inputs;

/// <summary>
/// Host CPU, memory and GPU on Linux, read out of /proc and /sys.
///
/// The Windows collector uses kernel32, which has no equivalent here, so these were simply absent
/// - CPU and RAM registers read a confident zero on Linux, which on an HMI gauge is
/// indistinguishable from an idle machine. Nothing here needs a package: /proc is a filesystem.
/// </summary>
internal static class LinuxHostStats
{
    /// <summary>Cumulative jiffies since boot, from the aggregate "cpu" line of /proc/stat.</summary>
    internal readonly record struct CpuTimes(ulong Idle, ulong Total)
    {
        public bool IsValid => Total > 0;
    }

    /// <summary>
    /// Fields are: user nice system idle iowait irq softirq steal guest guest_nice.
    ///
    /// Idle counts idle + iowait; a core waiting on disk is not doing work. Guest time is already
    /// included in user, so summing every field would double-count it - the two guest fields are
    /// dropped rather than added.
    /// </summary>
    public static CpuTimes ReadCpuTimes()
    {
        try
        {
            using var reader = new StreamReader("/proc/stat");
            var line = reader.ReadLine();
            if (line is null || !line.StartsWith("cpu ", StringComparison.Ordinal))
                return default;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            ulong total = 0, idle = 0;
            for (var i = 1; i < parts.Length && i <= 8; i++)   // stop before guest and guest_nice
            {
                if (!ulong.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    continue;
                total += value;
                if (i is 4 or 5) idle += value;               // idle, iowait
            }
            return new CpuTimes(idle, total);
        }
        catch
        {
            return default;
        }
    }

    internal readonly record struct MemoryInfo(double TotalGb, double UsedGb, double UsedPercent)
    {
        public bool IsValid => TotalGb > 0;
    }

    /// <summary>
    /// From /proc/meminfo, in kB.
    ///
    /// Uses MemAvailable rather than MemFree. MemFree excludes the page cache, which Linux fills
    /// on purpose, so a healthy machine looks like it is out of memory - reporting 95% used on an
    /// idle box is how that mistake shows up on a gauge. MemAvailable is the kernel's own estimate
    /// of what a new allocation could actually get. It has existed since 3.14; MemFree is the
    /// fallback if it is missing.
    /// </summary>
    public static MemoryInfo ReadMemory()
    {
        try
        {
            ulong total = 0, available = 0, free = 0;
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (TryField(line, "MemTotal:", out var t)) total = t;
                else if (TryField(line, "MemAvailable:", out var a)) available = a;
                else if (TryField(line, "MemFree:", out var f)) free = f;
                if (total > 0 && available > 0) break;
            }

            if (total == 0) return default;
            if (available == 0) available = free;

            const double kbPerGb = 1024d * 1024d;
            var totalGb = total / kbPerGb;
            var usedGb = (total - Math.Min(available, total)) / kbPerGb;
            return new MemoryInfo(totalGb, usedGb, usedGb / totalGb * 100.0);
        }
        catch
        {
            return default;
        }
    }

    private static bool TryField(string line, string prefix, out ulong value)
    {
        value = 0;
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var rest = line.AsSpan(prefix.Length).Trim();
        var end = rest.IndexOf(' ');
        if (end > 0) rest = rest[..end];
        return ulong.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// GPU busy percentage, where the driver exposes one.
    ///
    /// There is no equivalent of Windows' PDH "GPU Engine" counters, and each vendor reports
    /// differently. AMD exposes gpu_busy_percent directly in sysfs, which is the one case that can
    /// be read without a vendor library or spawning a process. Intel needs perf counters and NVIDIA
    /// needs NVML, so both return null and the tag is left alone rather than being told zero.
    /// </summary>
    public static double? ReadGpuPercent()
    {
        try
        {
            if (!Directory.Exists("/sys/class/drm")) return null;

            foreach (var card in Directory.GetDirectories("/sys/class/drm", "card?"))
            {
                var path = Path.Combine(card, "device", "gpu_busy_percent");
                if (!File.Exists(path)) continue;
                var text = File.ReadAllText(path).Trim();
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
                    return Math.Clamp(percent, 0, 100);
            }
        }
        catch
        {
            // A GPU that will not report is not an error worth logging every second.
        }
        return null;
    }

    /// <summary>Seconds since boot, from /proc/uptime. Host uptime, not this process's.</summary>
    public static double? ReadUptimeSeconds()
    {
        try
        {
            var text = File.ReadAllText("/proc/uptime").Split(' ')[0];
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                ? seconds
                : null;
        }
        catch
        {
            return null;
        }
    }
}
