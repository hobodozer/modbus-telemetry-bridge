using ModbusBridge.Core.Outputs.VJoy;

namespace ModbusBridge.SmokeTest;

/// <summary>
/// Exercises the real vJoy driver and reads the result back through winmm, which proves the
/// P/Invoke signatures and report struct layout rather than assuming them.
/// Skips cleanly (without failing) when vJoy is not installed or the device is owned elsewhere.
/// </summary>
internal static class VJoyChecks
{
    public static void Run(Action<bool, string> check, Action<string> section, Action<string> note)
    {
        section("vJoy driver");

        if (!VJoyInterop.IsAvailable)
        {
            note($"SKIPPED - {VJoyInterop.LoadError}");
            return;
        }

        var version = VJoyInterop.GetvJoyVersion();
        check(true, $"vJoy available: {VJoyInterop.ProductString()} " +
                    $"by {VJoyInterop.ManufacturerString()}, API version 0x{version:X}");

        ushort dllVersion = 0, driverVersion = 0;
        var matched = VJoyInterop.DriverMatch(ref dllVersion, ref driverVersion);
        check(matched, $"DLL 0x{dllVersion:X} and driver 0x{driverVersion:X} versions " +
                       (matched ? "match" : "DO NOT match - reinstall vJoy"));

        // Find a device we can actually use.
        uint deviceId = 0;
        for (uint candidate = 1; candidate <= 16; candidate++)
        {
            var status = VJoyInterop.GetVJDStatus(candidate);
            if (status is VjdStatus.Free or VjdStatus.Own) { deviceId = candidate; break; }
            if (status == VjdStatus.Busy) note($"device {candidate} is busy (owned by another app)");
        }

        if (deviceId == 0)
        {
            note("SKIPPED - no free vJoy device. Add one in vJoyConf, or close whatever owns them.");
            return;
        }

        using var device = VJoyDevice.Open(deviceId, out var error);
        if (device is null)
        {
            check(false, $"could not open vJoy device {deviceId}: {error}");
            return;
        }

        check(true, $"acquired {device.Capabilities}");

        if (device.Capabilities.ButtonCount < 60)
            note($"NOTE: device {deviceId} is configured for {device.Capabilities.ButtonCount} buttons. " +
                 "Raise this in vJoyConf to match your panel (the bridge does not care either way).");

        var candidates = JoystickReader.ListLive();
        note($"winmm reports {candidates.Count} live joystick(s): " +
             string.Join(", ", candidates.Select(d => $"[{d.Index}] '{d.Name}' {d.NumButtons}btn/{d.NumAxes}ax")));

        // Identify by behaviour, not by name: winmm's product string is usually a generic
        // "Microsoft PC-joystick driver" rather than anything mentioning vJoy.
        var reader = JoystickReader.IdentifyByProbe(
            candidates,
            pressProbe: () => { device.ClearAll(); device.SetButton(1, true); device.Flush(); },
            releaseProbe: () => { device.ClearAll(); device.Flush(); });

        if (reader is null)
        {
            check(false, "no winmm joystick responded to a vJoy button press - " +
                         "the report reached the driver but did not surface as HID input");
            return;
        }

        check(true, $"identified the fed device as winmm joystick [{reader.Index}] '{reader.Name}' " +
                    $"({reader.NumButtons} buttons, {reader.NumAxes} axes, X range {reader.XMin}-{reader.XMax})");

        // ---- Buttons: prove the bitfield layout, including the >32 boundary ----
        var buttonsToTest = new[] { 1, 2, 8, 17, 32 }
            .Where(b => b <= device.Capabilities.ButtonCount)
            .ToArray();

        foreach (var button in buttonsToTest)
        {
            device.ClearAll();
            device.SetButton(button, true);
            check(device.Flush(), $"UpdateVJD accepted button {button} press");

            Thread.Sleep(40);
            var reading = JoystickReader.Read(reader.Index);
            if (reading is null) { check(false, "winmm read failed"); continue; }

            check(JoystickReader.ButtonPressed(reading, button),
                  $"button {button} pressed reads back as pressed");

            // Nothing else should have moved - catches an off-by-one or a wrong bank.
            var others = Enumerable.Range(1, Math.Min(32, (int)reader.NumButtons))
                                   .Where(b => b != button)
                                   .Count(b => JoystickReader.ButtonPressed(reading, b));
            check(others == 0, $"no other button set while {button} is pressed (found {others})");
        }

        // Releasing must actually release.
        device.ClearAll();
        device.Flush();
        Thread.Sleep(40);
        var released = JoystickReader.Read(reader.Index);
        check(released is not null && released.Buttons == 0, "all buttons released after ClearAll");

        // Buttons above 32 exercise the extended banks. winmm cannot see them, but the driver
        // rejecting the report would show up as a failed update.
        if (device.Capabilities.ButtonCount >= 64)
        {
            device.ClearAll();
            device.SetButton(33, true);
            device.SetButton(64, true);
            check(device.Flush(), "UpdateVJD accepted buttons 33 and 64 (extended banks)");
            check(device.GetButton(33) && device.GetButton(64), "extended-bank bits set in the report");
            check(!device.GetButton(1) && !device.GetButton(32), "extended banks did not bleed into buttons 1-32");
            device.ClearAll();
            device.Flush();
        }
        else
        {
            note($"device has {device.Capabilities.ButtonCount} buttons; skipping the >32 bank check");
        }

        // ---- Axes: prove each axis lands where it was aimed ----
        foreach (var axis in new[] { VJoyAxis.X, VJoyAxis.Y, VJoyAxis.Z })
        {
            if (!device.Capabilities.HasAxis(axis)) { note($"axis {axis} not configured; skipped"); continue; }

            foreach (var target in new[] { 0.0, 0.25, 0.75, 1.0 })
            {
                device.SetAxis(axis, device.ScaleToAxis(axis, target));
                device.Flush();
                Thread.Sleep(40);

                var reading = JoystickReader.Read(reader.Index);
                if (reading is null) { check(false, "winmm read failed"); break; }

                // winmm rescales to its own range, so compare proportionally.
                var (raw, min, max) = axis switch
                {
                    VJoyAxis.X => (reading.XPos, reader.XMin, reader.XMax),
                    VJoyAxis.Y => (reading.YPos, reader.YMin, reader.YMax),
                    _ => (reading.ZPos, reader.ZMin, reader.ZMax)
                };
                var actual = max > min ? (raw - (double)min) / (max - min) : 0;

                check(Math.Abs(actual - target) < 0.02,
                      $"axis {axis} commanded {target:P0}, reads back {actual:P1}");
            }

            device.CentreAllAxes();
            device.Flush();
        }

        // ---- Throughput: this sits in the PLC-contact-to-game path ----
        const int iterations = 2000;
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var i = 0; i < iterations; i++)
        {
            device.SetButton(1, i % 2 == 0);
            device.Flush();
        }
        var elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0
                        / System.Diagnostics.Stopwatch.Frequency;

        device.ClearAll();
        device.Flush();

        var perUpdate = elapsedMs / iterations;
        check(perUpdate < 1.0, $"UpdateVJD costs {perUpdate:0.000} ms per full report " +
                               $"({iterations} updates in {elapsedMs:0} ms)");
        check(device.UpdateFailures == 0, $"{device.UpdateFailures} failed update(s)");
    }
}
