using ModbusBridge.Core.Config;
using ModbusBridge.Core.Diagnostics;
using ModbusBridge.Core.Tags;

namespace ModbusBridge.Core.Outputs.Keyboard;

internal sealed class KeyRuntime
{
    public required KeyMapping Config { get; init; }
    public required TagEntry Tag { get; init; }
    public required List<MacroStep> Steps { get; init; }

    public bool LastInput;
    public bool HasSeenInput;
    public bool Held;
    public long NextRepeatTicks;
    public Task? Macro;
}

/// <summary>
/// Drives keyboard input from tags, for games that ignore joystick input for some functions.
///
/// Sends nothing unless <see cref="KeyboardConfig.DryRun"/> is turned off. That default is
/// deliberate: this types into whatever window has focus, which during setup is usually the
/// configuration UI rather than the game.
/// </summary>
public sealed class KeyboardFeeder : IAsyncDisposable
{
    private readonly KeyboardConfig _config;
    private readonly TagBus _bus;
    private readonly List<KeyRuntime> _keys = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public IKeySink Sink { get; }
    public bool IsRunning => _loop is not null;
    public IReadOnlyList<string> Warnings { get; private set; } = Array.Empty<string>();

    public KeyboardFeeder(KeyboardConfig config, TagBus bus, IKeySink? sink = null)
    {
        _config = config;
        _bus = bus;
        Sink = sink ?? (config.DryRun ? new DryRunKeySink() : new SendInputKeySink());
    }

    public void Start()
    {
        if (_loop is not null) return;

        var warnings = new List<string>();

        foreach (var mapping in _config.Mappings.Where(m => m.Enabled && !string.IsNullOrWhiteSpace(m.Tag)))
        {
            if (!KeySpec.TryParseSequence(mapping.Keys, out var steps, out var error))
            {
                // One bad key definition should not stop the rest from working.
                warnings.Add($"'{mapping.Tag}' -> '{mapping.Keys}': {error}");
                continue;
            }

            if (mapping.Mode == KeyMode.Hold && steps.Count != 1)
            {
                warnings.Add($"'{mapping.Tag}': Hold needs exactly one key, not a sequence.");
                continue;
            }

            _keys.Add(new KeyRuntime { Config = mapping, Tag = _bus.GetOrAdd(mapping.Tag), Steps = steps });
        }

        Warnings = warnings;
        foreach (var warning in warnings) Log.Warn("keyboard", warning);

        if (_keys.Count == 0)
        {
            if (_config.Mappings.Count > 0) Log.Warn("keyboard", "No usable key mappings.");
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));

        Log.Info("keyboard", $"Driving {_keys.Count} key mapping(s) every {_config.UpdateIntervalMs} ms" +
                             (_config.DryRun ? " (DRY RUN - nothing is actually typed)." : "."));
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;

        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _cts.Dispose();
        _cts = null;
        _loop = null;

        ReleaseAll();
        Log.Info("keyboard", "Stopped.");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>Lifts anything still held. A key left down outlives the process that pressed it.</summary>
    public void ReleaseAll()
    {
        foreach (var key in _keys.Where(k => k.Held))
        {
            ReleaseKeystroke(key.Steps[0].Key!.Value);
            key.Held = false;
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var interval = Math.Max(1, _config.UpdateIntervalMs);

        while (!ct.IsCancellationRequested)
        {
            try { Evaluate(ct); }
            catch (Exception ex) { Log.Warn("keyboard", $"Evaluation failed: {ex.Message}"); }

            try { await Task.Delay(interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        ReleaseAll();
    }

    private void Evaluate(CancellationToken ct)
    {
        var now = Clock.Ticks;

        foreach (var key in _keys)
        {
            var value = key.Tag.Value;

            // A key held because of a value that has since gone bad is exactly the stuck-key case,
            // so quality is a release condition, not merely a reason to skip.
            var good = value.Quality >= Data.TagQuality.Good;
            var input = good && (value.Number >= key.Config.Threshold) ^ key.Config.Invert;
            if (!good) input = false;

            switch (key.Config.Mode)
            {
                case KeyMode.Hold:
                    if (input && !key.Held) { PressKeystroke(key.Steps[0].Key!.Value); key.Held = true; }
                    else if (!input && key.Held) { ReleaseKeystroke(key.Steps[0].Key!.Value); key.Held = false; }
                    break;

                case KeyMode.Tap:
                    if (input && !key.LastInput && key.HasSeenInput) TapAll(key);
                    else if (input && key.Config.RepeatMs > 0 && now >= key.NextRepeatTicks)
                    {
                        TapAll(key);
                        key.NextRepeatTicks = now + Clock.FromMs(key.Config.RepeatMs);
                    }
                    if (input && !key.LastInput) key.NextRepeatTicks = now + Clock.FromMs(Math.Max(1, key.Config.RepeatMs));
                    break;

                case KeyMode.Macro:
                    // Runs off the evaluation loop because a macro contains waits, and blocking
                    // here would stall every other mapping.
                    if (input && !key.LastInput && key.HasSeenInput && (key.Macro is null || key.Macro.IsCompleted))
                        key.Macro = Task.Run(() => RunMacroAsync(key, ct), ct);
                    break;
            }

            key.LastInput = input;
            key.HasSeenInput = true;
        }
    }

    private void TapAll(KeyRuntime key)
    {
        foreach (var step in key.Steps)
        {
            if (step.Key is not { } stroke) continue;
            PressKeystroke(stroke);
            ReleaseKeystroke(stroke);
        }
    }

    private async Task RunMacroAsync(KeyRuntime key, CancellationToken ct)
    {
        foreach (var step in key.Steps)
        {
            if (ct.IsCancellationRequested) return;

            if (step.Key is { } stroke)
            {
                // The release must happen even if the wait is cancelled, or stopping the engine
                // mid-macro leaves the key physically down - and it outlives this process.
                PressKeystroke(stroke);
                try
                {
                    await Task.Delay(Math.Max(1, _config.KeyPressMs), ct).ConfigureAwait(false);
                }
                finally
                {
                    ReleaseKeystroke(stroke);
                }

                try { await Task.Delay(Math.Max(1, _config.KeyGapMs), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
            else if (step.DelayMs > 0)
            {
                await Task.Delay(step.DelayMs, ct).ConfigureAwait(false);
            }
        }
    }

    // Modifiers are pressed around the key so a game sees a genuine chord rather than bare keys.
    private void PressKeystroke(Keystroke key)
    {
        foreach (var modifier in Modifiers(key)) Sink.Down(modifier);
        Sink.Down(key);
    }

    private void ReleaseKeystroke(Keystroke key)
    {
        Sink.Up(key);
        foreach (var modifier in Modifiers(key).Reverse()) Sink.Up(modifier);
    }

    private static IEnumerable<Keystroke> Modifiers(Keystroke key)
    {
        if (key.Ctrl) yield return new Keystroke(0x11, "ctrl", false, false, false, false);
        if (key.Alt) yield return new Keystroke(0x12, "alt", false, false, false, false);
        if (key.Shift) yield return new Keystroke(0x10, "shift", false, false, false, false);
        if (key.Win) yield return new Keystroke(0x5B, "win", false, false, false, false);
    }
}
