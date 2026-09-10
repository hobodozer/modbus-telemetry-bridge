using ModbusBridge.Core.Engine;
using ModbusBridge.Core.Outputs.VJoy;
using ModbusBridge.Core.Tags;

namespace ModbusBridge.SmokeTest;

/// <summary>
/// The path that matters most for a sim rig: a tag changes, the feeder notices, vJoy reports it, and
/// Windows sees a real HID button press. Everything is driven through the live engine and verified
/// through winmm, so nothing here is checking its own homework.
/// </summary>
internal static class VJoyEndToEndChecks
{
    public static async Task RunAsync(BridgeEngine engine, Action<bool, string> check,
                                      Action<string> section, Action<string> note)
    {
        section("Tag bus -> vJoy -> Windows");

        if (!VJoyInterop.IsAvailable)
        {
            note($"SKIPPED - {VJoyInterop.LoadError}");
            return;
        }

        var feeder = engine.VJoyFeeders.FirstOrDefault();
        if (feeder is null)
        {
            note("SKIPPED - the engine started no vJoy feeder (device busy or missing).");
            return;
        }

        if (!feeder.IsRunning)
        {
            check(false, $"vJoy feeder did not start: {feeder.Error}");
            return;
        }

        check(true, $"feeder running on device {feeder.DeviceId} ({feeder.Capabilities})");
        foreach (var warning in feeder.Warnings) note($"mapping warning: {warning}");

        var buttonTag = engine.Tags.GetOrAdd("vjoy.testButton");
        var toggleTag = engine.Tags.GetOrAdd("vjoy.testToggle");
        var pulseTag = engine.Tags.GetOrAdd("vjoy.testPulse");
        var ncTag = engine.Tags.GetOrAdd("vjoy.testNc");
        var axisTag = engine.Tags.GetOrAdd("vjoy.testAxis");

        // Give every mapped tag a good value so the feeder leaves its released state.
        foreach (var tag in new[] { buttonTag, toggleTag, pulseTag, ncTag })
            tag.Force(TagValue.Good(false));
        axisTag.Force(TagValue.Good(50.0));
        await Task.Delay(120);

        var candidates = JoystickReader.ListLive();
        var reader = JoystickReader.IdentifyByProbe(
            candidates,
            pressProbe: () => buttonTag.Force(TagValue.Good(true)),
            releaseProbe: () => buttonTag.Force(TagValue.Good(false)),
            settleMs: 120);

        if (reader is null)
        {
            check(false, "no winmm joystick responded to a tag change driven through the feeder");
            return;
        }

        check(true, $"tag change reached Windows as HID input on joystick [{reader.Index}]");

        // ---- Momentary: held exactly as long as the tag is true ----
        buttonTag.Force(TagValue.Good(true));
        await Task.Delay(120);
        check(IsPressed(reader.Index, 1), "momentary button 1 is pressed while its tag is true");

        buttonTag.Force(TagValue.Good(false));
        await Task.Delay(120);
        check(!IsPressed(reader.Index, 1), "momentary button 1 releases when its tag goes false");

        // ---- Normally-closed: inverted sense, the software NO/NC toggle at the vJoy end ----
        if (feeder.Capabilities!.ButtonCount >= 4)
        {
            ncTag.Force(TagValue.Good(false));
            await Task.Delay(120);
            check(IsPressed(reader.Index, 4), "inverted button 4 reads pressed while its tag is false");

            ncTag.Force(TagValue.Good(true));
            await Task.Delay(120);
            check(!IsPressed(reader.Index, 4), "inverted button 4 releases when its tag goes true");
        }
        else
        {
            note($"device has {feeder.Capabilities.ButtonCount} buttons; skipping the inverted-button check");
        }

        // ---- Toggle: each rising edge flips, and it stays flipped ----
        if (feeder.Capabilities.ButtonCount >= 2)
        {
            toggleTag.Force(TagValue.Good(true));
            await Task.Delay(120);
            toggleTag.Force(TagValue.Good(false));
            await Task.Delay(120);
            check(IsPressed(reader.Index, 2), "toggle button 2 latches on after one press and stays on");

            toggleTag.Force(TagValue.Good(true));
            await Task.Delay(120);
            toggleTag.Force(TagValue.Good(false));
            await Task.Delay(120);
            check(!IsPressed(reader.Index, 2), "toggle button 2 latches off on the second press");
        }

        // ---- Pulse: fixed-length press regardless of how long the contact is held ----
        if (feeder.Capabilities.ButtonCount >= 3)
        {
            pulseTag.Force(TagValue.Good(true));
            await Task.Delay(50);
            var duringPulse = IsPressed(reader.Index, 3);

            // Hold the input well past the pulse length; the button must have let go anyway.
            await Task.Delay(250);
            var afterPulse = IsPressed(reader.Index, 3);
            pulseTag.Force(TagValue.Good(false));

            check(duringPulse, "pulse button 3 fires on the rising edge");
            check(!afterPulse, "pulse button 3 releases after its 120 ms window even though the tag is still true");
        }

        // ---- Axis: engineering value 0..100 maps across full travel ----
        if (feeder.Capabilities.HasAxis(VJoyAxis.X))
        {
            foreach (var (value, expected) in new[] { (0.0, 0.0), (25.0, 0.25), (100.0, 1.0) })
            {
                axisTag.Force(TagValue.Good(value));
                await Task.Delay(120);

                var reading = JoystickReader.Read(reader.Index);
                if (reading is null) { check(false, "winmm read failed"); break; }

                var actual = reader.XMax > reader.XMin
                    ? (reading.XPos - (double)reader.XMin) / (reader.XMax - reader.XMin)
                    : 0;
                check(Math.Abs(actual - expected) < 0.02,
                      $"axis tag {value} (of 0..100) reads back at {actual:P1} travel, expected {expected:P0}");
            }
        }

        // ---- Failsafe: a dead PLC must not leave a button held down ----
        buttonTag.Force(TagValue.Good(true));
        await Task.Delay(120);
        check(IsPressed(reader.Index, 1), "button held ahead of the bad-quality check");

        // ---- Hat driven by four contacts ----
        // Read back through winmm's own dwPOV rather than the report struct, for the same reason
        // the buttons are: a wrong field or a wrong pool would still "succeed" on write.
        var hatPov = feeder.Capabilities is { } caps && caps.DiscretePovCount + caps.ContinuousPovCount > 0;
        if (!hatPov)
        {
            note("device has no POV configured in vJoyConf; skipping the hat checks");
        }
        else
        {
            var up = engine.Tags.GetOrAdd("vjoy.testHatUp");
            var right = engine.Tags.GetOrAdd("vjoy.testHatRight");
            var down = engine.Tags.GetOrAdd("vjoy.testHatDown");
            var left = engine.Tags.GetOrAdd("vjoy.testHatLeft");
            var contacts = new[] { up, right, down, left };

            foreach (var tag in contacts) tag.Force(TagValue.Good(false));
            await Task.Delay(150);
            check(JoystickReader.Read(reader.Index)?.Pov is null or 0xFFFF,
                  "hat reads centred while no direction contact is closed");

            // winmm reports hundredths of a degree; 0xFFFF is centred.
            async Task<uint?> HatAfter(params TagEntry[] pressed)
            {
                foreach (var tag in contacts) tag.Force(TagValue.Good(false));
                foreach (var tag in pressed) tag.Force(TagValue.Good(true));
                await Task.Delay(150);
                return JoystickReader.Read(reader.Index)?.Pov;
            }

            var cardinals = new (TagEntry Tag, uint Degrees, string Name)[]
            {
                (up, 0, "up"), (right, 9000, "right"), (down, 18000, "down"), (left, 27000, "left")
            };

            foreach (var (tag, degrees, name) in cardinals)
            {
                var pov = await HatAfter(tag);
                check(pov == degrees, $"hat {name} contact reads back as {degrees / 100}deg (got " +
                                      (pov is null or 0xFFFF ? "centred" : $"{pov / 100}deg") + ")");
            }

            // Opposing contacts cannot both be true on a real gate; the feeder cancels them.
            var opposed = await HatAfter(up, down);
            check(opposed is null or 0xFFFF, "opposing contacts (up+down) cancel to centred");

            // Diagonals exist only on a continuous hat; a discrete one rounds to a cardinal.
            var diagonal = await HatAfter(up, right);
            if (feeder.Capabilities!.ContinuousPovCount > 0)
                check(diagonal == 4500, $"up+right reads back as 45deg (got {diagonal})");
            else
                check(diagonal is 0 or 9000,
                      $"up+right rounds to a cardinal on a discrete hat (got {diagonal})");

            foreach (var tag in contacts) tag.Force(TagValue.Good(false));
            await Task.Delay(120);
        }

        foreach (var tag in new[] { buttonTag, toggleTag, pulseTag, ncTag, axisTag })
        {
            tag.Unforce();
            tag.DemoteQuality(ModbusBridge.Core.Data.TagQuality.Bad);
        }
        await Task.Delay(300);
        check(!IsPressed(reader.Index, 1),
              "all controls released when every driving tag went bad (PLC offline failsafe)");

        note($"feeder loop interval {feeder.Statistics.LoopIntervalMs:0.00} ms " +
             $"(worst {feeder.Statistics.MaxLoopIntervalMs:0.00} ms), " +
             $"{feeder.Statistics.Updates} update(s), {feeder.Statistics.Failures} failure(s)");
        check(feeder.Statistics.Failures == 0, $"{feeder.Statistics.Failures} failed vJoy update(s)");
    }

    private static bool IsPressed(uint index, int button)
    {
        var reading = JoystickReader.Read(index);
        return reading is not null && JoystickReader.ButtonPressed(reading, button);
    }
}
