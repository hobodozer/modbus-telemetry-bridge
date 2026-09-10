using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ModbusBridge.Core.Diagnostics;

/// <summary>
/// High-resolution timing. <see cref="Environment.TickCount64"/> and <c>DateTime.UtcNow</c> both
/// advance in ~15.6 ms steps on Windows, which is useless when the target poll interval is 10 ms -
/// they would quantise every measurement to 0 ms or 16 ms. Everything on the hot path uses the
/// stopwatch counter instead.
/// </summary>
public static class Clock
{
    private static readonly double TicksPerMs = Stopwatch.Frequency / 1000.0;

    /// <summary>Monotonic high-resolution counter.</summary>
    public static long Ticks => Stopwatch.GetTimestamp();

    public static double ToMs(long ticks) => ticks / TicksPerMs;

    public static long FromMs(double ms) => (long)(ms * TicksPerMs);

    public static double MsSince(long startTicks) => (Stopwatch.GetTimestamp() - startTicks) / TicksPerMs;

    public static bool IsHighResolution => Stopwatch.IsHighResolution;

    public static long Frequency => Stopwatch.Frequency;
}

/// <summary>
/// Raises the Windows multimedia timer resolution to 1 ms for the life of the process, so waits
/// land close to where they were asked to. Without it a 10 ms poll interval actually runs at
/// ~15.6 ms. Costs a small amount of extra power draw, so it is configurable.
/// </summary>
public sealed class TimerResolutionScope : IDisposable
{
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint milliseconds);

    private readonly uint _period;
    private bool _active;

    public TimerResolutionScope(uint periodMs = 1)
    {
        _period = periodMs;
        OptOutOfTimerThrottling();
        try
        {
            _active = TimeBeginPeriod(_period) == 0;
            Log.Info("clock", _active
                ? $"System timer resolution raised to {_period} ms."
                : $"Could not raise the system timer resolution; waits will quantise to ~15.6 ms.");
        }
        catch (DllNotFoundException)
        {
            Log.Warn("clock", "winmm.dll unavailable; leaving the system timer resolution alone.");
        }
    }

    public bool IsActive => _active;

    /// <summary>
    /// Tells Windows not to power-throttle this process's timer resolution.
    ///
    /// Since Windows 10 2004 timer resolution is per-process, and Windows throttles it for
    /// processes it considers background - which a bridge running behind a full-screen game
    /// always is. When that happens timeBeginPeriod is quietly ignored, every wait quantises to
    /// ~15.6 ms, and the only thing still holding the poll interval is the spin in PreciseDelay.
    /// Opting out costs nothing and makes the timing independent of which window has focus.
    /// </summary>
    private static void OptOutOfTimerThrottling()
    {
        try
        {
            // ControlMask says which behaviours we are managing; a cleared StateMask bit means
            // "do not throttle". Setting both would ask for MORE throttling, not less.
            var state = new ProcessPowerThrottlingState
            {
                Version = ProcessPowerThrottlingCurrentVersion,
                ControlMask = ProcessPowerThrottlingIgnoreTimerResolution,
                StateMask = 0
            };

            var ok = SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling,
                                           ref state, Marshal.SizeOf<ProcessPowerThrottlingState>());
            if (!ok)
                Log.Info("clock", "Could not opt out of timer-resolution throttling; " +
                                  "timing may quantise while this process is in the background.");
        }
        catch (EntryPointNotFoundException)
        {
            // Older Windows has no such throttling to opt out of.
        }
        catch (DllNotFoundException) { }
    }

    private const int ProcessPowerThrottling = 4;
    private const uint ProcessPowerThrottlingCurrentVersion = 1;
    private const uint ProcessPowerThrottlingIgnoreTimerResolution = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(IntPtr process, int informationClass,
                                                     ref ProcessPowerThrottlingState information,
                                                     int informationSize);

    public void Dispose()
    {
        if (!_active) return;
        try { TimeEndPeriod(_period); } catch { }
        _active = false;
    }
}

/// <summary>Waits that stay accurate below the OS scheduler's granularity.</summary>
public static class PreciseDelay
{
    /// <summary>
    /// How much of each wait is spun rather than slept. The spin exists because Task.Delay rounds
    /// up to the system tick; it only needs to cover the scheduler's overshoot, not 15.6 ms.
    /// Measure on the wire after changing this - the smoke test's timing check is unreliable,
    /// because timer resolution is per-process since Windows 10 2004 and a background test
    /// process does not get what the app gets.
    /// </summary>
    public static double SpinHeadroomMs = 0.5;

    /// <summary>
    /// Sleeps for <paramref name="milliseconds"/>. Longer waits hand the thread back to the
    /// scheduler; the last stretch is spun so the wake-up is not rounded up to the next tick.
    /// </summary>
    public static async Task WaitAsync(double milliseconds, CancellationToken ct)
    {
        if (milliseconds <= 0) return;

        var deadline = Clock.Ticks + Clock.FromMs(milliseconds);

        // Leave ~1.5 ms of headroom for the scheduler to overshoot into. This looks wasteful -
        // it is 75% of a 2 ms vJoy update - but shrinking it was tried and reverted: Task.Delay
        // rounds up to the ~15.6 ms system tick, so the spin is the only thing holding a 10 ms
        // poll at 10.0 ms. The smoke test asserts that and caught the regression immediately.
        // The cost is inherent to sub-tick precision; raise the caller's interval to reduce it.
        var coarseMs = milliseconds - SpinHeadroomMs;
        if (coarseMs > 0)
            await Task.Delay(TimeSpan.FromMilliseconds(coarseMs), ct).ConfigureAwait(false);

        var spinner = new SpinWait();
        while (Clock.Ticks < deadline)
        {
            ct.ThrowIfCancellationRequested();
            spinner.SpinOnce(-1);   // -1 keeps it from sleeping, which would defeat the point
        }
    }
}
