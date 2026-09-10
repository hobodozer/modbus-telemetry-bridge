using ModbusBridge.Core.Config;
using ModbusBridge.Core.Data;
using ModbusBridge.Core.Diagnostics;
using ModbusBridge.Core.Engine;
using ModbusBridge.Core.Modbus;
using ModbusBridge.Core.Outputs.VJoy;

namespace ModbusBridge.SmokeTest;

/// <summary>
/// End-to-end check with no hardware: a virtual PLC feeds a device runner, the runner publishes to
/// the tag bus, the tag bus feeds the HMI-facing server, and a real Modbus client reads it back.
/// Exits non-zero if anything fails, so it can be wired into a build.
/// </summary>
internal static class Program
{
    private const int VirtualPlcPort = 15020;
    private const int HmiServerPort = 15502;
    private const int TelemetryPort = 15601;

    private static int _failures;

    private static async Task<int> Main()
    {
        Log.MinimumLevel = LogLevel.Info;
        Log.Entry += entry => Console.WriteLine($"  [{entry.Level,-5}] {entry.Source}: {entry.Message}");

        // Low-level vJoy checks run first and release the device, so the engine's feeder can own it.
        VJoyChecks.Run(Check, Section, Note);

        var engine = new BridgeEngine(BuildConfig());
        await engine.StartAsync();

        Console.WriteLine("\nWaiting for the device to connect and poll...");
        await WaitForAsync(() => engine.Devices.All(d => d.State == DeviceState.Online), TimeSpan.FromSeconds(10),
                           "device runner reached Online");

        // Let a few poll cycles land so tags are populated.
        await Task.Delay(1500);

        Section("Tag bus");
        var tagCount = engine.Tags.Count;
        Check(tagCount > 0, $"tag bus has {tagCount} tag(s)");
        var goodTags = engine.Tags.All.Count(t => t.Value.Quality == TagQuality.Good);
        Check(goodTags > 0, $"{goodTags} tag(s) are Good");

        Section("PLC -> tag bus");
        var device = engine.Devices[0];
        Check(device.Statistics.Polls > 0, $"completed {device.Statistics.Polls} poll cycle(s)");
        Check(device.Statistics.Errors == 0, $"{device.Statistics.Errors} error(s)");
        Console.WriteLine($"    round-trip last={device.Statistics.LastLatencyMs:0.0} ms " +
                          $"avg={device.Statistics.AverageLatencyMs:0.0} ms " +
                          $"min={device.Statistics.MinLatencyMs:0.0} ms " +
                          $"max={device.Statistics.MaxLatencyMs:0.0} ms " +
                          $"jitter={device.Statistics.JitterMs:0.0} ms");
        foreach (var timing in device.ReadGroupTimings())
            Console.WriteLine($"    group '{timing.Name}': wire {timing.LastLatencyMs:0.0} ms, " +
                              $"actual cycle {timing.ActualCycleMs:0.0} ms " +
                              $"(configured {timing.ConfiguredIntervalMs} ms)");

        // Guards the high-resolution timer work: without timeBeginPeriod(1) every wait rounds up to
        // the ~15.6 ms scheduler tick, so a 10 ms group silently runs at 16 ms.
        Check(Clock.IsHighResolution, $"stopwatch is high resolution ({Clock.Frequency:N0} Hz)");
        foreach (var timing in device.ReadGroupTimings())
        {
            var overshoot = timing.ActualCycleMs - timing.ConfiguredIntervalMs;
            Check(overshoot < 4.0,
                  $"group '{timing.Name}' holds its {timing.ConfiguredIntervalMs} ms interval " +
                  $"(actual {timing.ActualCycleMs:0.0} ms, overshoot {overshoot:+0.0;-0.0} ms)");
        }

        var axisTag = engine.Tags.Find("plc.ai.axis1");
        Check(axisTag is not null, "tag 'plc.ai.axis1' exists");
        Check(axisTag!.Value.Quality == TagQuality.Good, $"axis1 quality is {axisTag.Value.Quality}");
        Console.WriteLine($"    plc.ai.axis1 = {axisTag.Value.Number:0.0000} (scaled from raw counts)");

        Section("Normally-closed inversion");
        var normal = engine.Tags.Find("plc.di.b00");
        var inverted = engine.Tags.Find("plc.di.b00_nc");
        Check(normal is not null && inverted is not null, "both the NO and NC views of bit 0 exist");
        Check(Math.Abs(normal!.Value.Number - inverted!.Value.Number) > 0.5,
              $"NO reads {normal.Value.Number} while NC reads {inverted.Value.Number}");

        Section("Tag bus -> HMI server");
        using var hmi = new ModbusTcpClient("127.0.0.1", HmiServerPort) { ResponseTimeoutMs = 2000 };
        await hmi.ConnectAsync(CancellationToken.None);
        Check(hmi.IsConnected, "HMI client connected to the bridge's server");

        var floats = await hmi.ReadHoldingRegistersAsync(1, 0, 8, CancellationToken.None);
        var speed = ValueCodec.Decode(floats.AsSpan(0), PointDataType.Float32, WordOrder.HighFirst, ByteOrder.HighFirst);
        var rpm = ValueCodec.Decode(floats.AsSpan(2), PointDataType.Float32, WordOrder.HighFirst, ByteOrder.HighFirst);
        var busSpeed = engine.Tags.GetNumber("sim.speedKph");
        Console.WriteLine($"    read back speed={speed:0.00} km/h, rpm={rpm:0.0}");
        Check(Math.Abs(speed - busSpeed) < 0.5, $"served speed {speed:0.00} matches tag {busSpeed:0.00}");
        Check(rpm is > 0 and < 10000, $"rpm {rpm:0.0} is in a sane range");

        var coils = await hmi.ReadCoilsAsync(1, 0, 16, CancellationToken.None);
        var plcBit = engine.Tags.GetNumber("plc.di.b00") != 0;
        Check(coils[0] == plcBit, $"PLC bit 0 reached the HMI map as {coils[0]}");

        Section("HMI writes -> tag bus");
        await hmi.WriteSingleCoilAsync(1, 100, true, CancellationToken.None);
        await Task.Delay(100);
        Check(engine.Tags.GetNumber("hmi.btn01") != 0, "HMI coil write landed on tag 'hmi.btn01'");

        await hmi.WriteMultipleRegistersAsync(1, 200, new ushort[] { 1234 }, CancellationToken.None);
        await Task.Delay(100);
        Check(Math.Abs(engine.Tags.GetNumber("hmi.setpoint") - 1234) < 0.001,
              $"HMI register write landed on tag 'hmi.setpoint' = {engine.Tags.GetNumber("hmi.setpoint")}");

        Section("Multiple concurrent clients");
        var extraClients = new List<ModbusTcpClient>();
        for (var i = 0; i < 4; i++)
        {
            var client = new ModbusTcpClient("127.0.0.1", HmiServerPort) { ResponseTimeoutMs = 2000 };
            await client.ConnectAsync(CancellationToken.None);
            extraClients.Add(client);
        }
        var reads = await Task.WhenAll(extraClients.Select(c =>
            c.ReadHoldingRegistersAsync(1, 0, 8, CancellationToken.None)));
        Check(reads.All(r => r.Length == 8), $"{extraClients.Count} extra clients all read successfully");
        Check(engine.Servers[0].Server.Sessions.Count == extraClients.Count + 1,
              $"server reports {engine.Servers[0].Server.Sessions.Count} live session(s)");
        foreach (var client in extraClients) client.Dispose();

        Section("Exception handling");
        try
        {
            await hmi.ReadHoldingRegistersAsync(1, 9000, 2, CancellationToken.None);
            Check(false, "reading an unmapped address should have thrown");
        }
        catch (ModbusProtocolException ex)
        {
            Check(ex.ExceptionCode == ModbusExceptionCode.IllegalDataAddress,
                  $"unmapped address returned exception {ex.ExceptionCode:X2} (illegal data address)");
        }

        Section("Codec round-trip");
        CheckCodec(PointDataType.Float32, 123.456, WordOrder.HighFirst, ByteOrder.HighFirst);
        CheckCodec(PointDataType.Float32, -987.5, WordOrder.LowFirst, ByteOrder.HighFirst);
        CheckCodec(PointDataType.Float32, 42.25, WordOrder.HighFirst, ByteOrder.Swapped);
        CheckCodec(PointDataType.Int32, -70000, WordOrder.LowFirst, ByteOrder.Swapped);
        CheckCodec(PointDataType.UInt32, 4000000000, WordOrder.HighFirst, ByteOrder.HighFirst);
        CheckCodec(PointDataType.Int16, -1234, WordOrder.HighFirst, ByteOrder.Swapped);
        CheckCodec(PointDataType.Float64, Math.PI, WordOrder.LowFirst, ByteOrder.HighFirst);

        await VJoyEndToEndChecks.RunAsync(engine, Check, Section, Note);
        await TelemetryChecks.RunAsync(engine, TelemetryPort, Check, Section, Note);

        Section("CSV record and replay");
        {
            var recordDir = Path.Combine(Path.GetTempPath(), "mbbridge-record-" + Guid.NewGuid().ToString("N")[..8]);
            var pattern = "rec.*";

            Check(ModbusBridge.Core.Diagnostics.TagRecorder.Matches("sim.*", "sim.speedKph"), "glob: prefix wildcard matches");
            Check(!ModbusBridge.Core.Diagnostics.TagRecorder.Matches("sim.*", "plc1.speed"), "glob: a non-match is rejected");
            Check(ModbusBridge.Core.Diagnostics.TagRecorder.Matches("*.estop", "plc1.di.estop"), "glob: suffix wildcard matches");
            Check(ModbusBridge.Core.Diagnostics.TagRecorder.Matches("*", "anything"), "glob: bare star matches everything");

            var a = engine.Tags.GetOrAdd("rec.number");
            var b = engine.Tags.GetOrAdd("rec.text");
            a.Set(ModbusBridge.Core.Tags.TagValue.Good(1), "test");
            b.Set(ModbusBridge.Core.Tags.TagValue.GoodText("hello, world"), "test");

            var recording = new RecordingConfig
            {
                Enabled = true, Directory = recordDir, FileName = "capture.csv",
                IntervalMs = 20, Tags = { }
            };
            recording.Tags.Clear();
            recording.Tags.Add(pattern);

            var recorder = new ModbusBridge.Core.Diagnostics.TagRecorder(recording, engine.Tags);
            recorder.Start();

            // Move the values while recording so the file holds a ramp rather than a constant.
            for (var i = 1; i <= 8; i++)
            {
                a.Set(ModbusBridge.Core.Tags.TagValue.Good(i * 10), "test");
                await Task.Delay(25);
            }
            await recorder.StopAsync();

            var file = Path.Combine(recordDir, "capture.csv");
            Check(File.Exists(file), "the recording file was written");
            var lines = File.ReadAllLines(file);
            Check(lines.Length > 2, $"the recording has data rows (got {lines.Length - 1})");
            Check(lines[0].StartsWith("elapsedMs,"), "the header starts with elapsedMs");
            // A value containing a comma has to survive the round trip.
            Check(lines.Any(l => l.Contains("\"hello, world\"")), "text with a comma is quoted");

            // Replay into a separate namespace so the originals are untouched and comparable.
            a.Set(ModbusBridge.Core.Tags.TagValue.Good(-1), "test");
            var replay = new ReplayConfig
            {
                Enabled = true, Path = file, Speed = 20, TagPrefix = "back."
            };
            var replayer = new ModbusBridge.Core.Inputs.TagReplayer(replay, engine.Tags);
            Check(replayer.Load(), "the recording loads");
            Check(replayer.ColumnCount == 2, $"both tags are columns (got {replayer.ColumnCount})");

            replayer.Start();
            await Task.Delay(1200);
            await replayer.StopAsync();

            var replayedNumber = engine.Tags.Find("back.rec.number");
            var replayedText = engine.Tags.Find("back.rec.text");
            Check(replayedNumber is not null && replayedNumber.Value.Number == 80,
                  $"replay ended on the last recorded value (got {replayedNumber?.Value.Number})");
            Check(replayedText?.Value.Text == "hello, world",
                  $"text replayed intact (got '{replayedText?.Value.Text}')");
            Check(a.Value.Number == -1, "replaying into a prefix left the original tag alone");

            try { Directory.Delete(recordDir, true); } catch { }
        }

        Section("Network scanner");
        {
            var cidr = ModbusBridge.Core.Modbus.ModbusScanner.ParseCidr("10.1.2.0/30");
            Check(cidr.Count == 2, $"a /30 expands to 2 usable hosts (got {cidr.Count})");
            Check(cidr[0].ToString() == "10.1.2.1", $"network address is skipped (first is {cidr[0]})");

            var single = ModbusBridge.Core.Modbus.ModbusScanner.ParseCidr("10.1.2.7");
            Check(single.Count == 1 && single[0].ToString() == "10.1.2.7", "a bare address expands to itself");

            // The scanner should find this test's own server, and report which areas are mapped.
            var self = new[] { System.Net.IPAddress.Loopback };
            var found = await ModbusBridge.Core.Modbus.ModbusScanner.ScanAsync(
                self, HmiServerPort, timeoutMs: 500);
            Check(found.Count == 1 && found[0].SpeaksModbus,
                  $"scan found the test server on port {HmiServerPort}");
            Check(found.Count == 1 && found[0].UnitIds.Contains((byte)1), "scan reported unit id 1");
            Check(found.Count == 1 && found[0].ReadableAreas is not null,
                  $"scan reported readable areas: {(found.Count == 1 ? found[0].ReadableAreas : "none")}");

            // A port with nothing on it must stay silent rather than reporting a phantom device.
            var quiet = await ModbusBridge.Core.Modbus.ModbusScanner.ScanAsync(
                self, HmiServerPort + 7, timeoutMs: 300);
            Check(quiet.Count == 0, "a closed port yields no result");
        }

        Section("Derived tag expressions");
        {
            double Tags(string name) => name switch
            {
                "a" => 10, "b" => 4, "level" => 1262, "capacity" => 1320, "zero" => 0, _ => 0
            };

            void Expect(string expression, double expected, string what)
            {
                if (!ModbusBridge.Core.Data.Expression.TryParse(expression, out var parsed, out var error))
                {
                    Check(false, $"{what}: parse failed - {error}");
                    return;
                }
                var actual = parsed!.Evaluate(Tags);
                Check(Math.Abs(actual - expected) < 1e-9, $"{what}: {expression} = {actual}");
            }

            Expect("1 + 2 * 3", 7, "precedence");
            Expect("(1 + 2) * 3", 9, "parentheses");
            Expect("-a + 2", -8, "unary minus");
            Expect("a / b", 2.5, "division");
            Expect("2 + 3 - 1", 4, "left associativity");
            Expect("level / capacity * 100", 1262d / 1320d * 100, "the fuel percent case");
            Expect("clamp(150, 0, 100)", 100, "clamp");
            Expect("round(2.567, 2)", 2.57, "round to places");
            Expect("if(a > b, 1, 2)", 1, "conditional");
            Expect("min(a, b) + max(a, b)", 14, "min and max");
            Expect("a > b && b > 0", 1, "boolean and");
            Expect("1e-3 * 1000", 1, "exponent literals");

            // Division by zero yields 0 rather than infinity, so a not-yet-populated tag cannot
            // put Inf on an HMI gauge.
            Expect("a / zero", 0, "divide by zero is contained");
            // Short-circuit: the right side must not be evaluated when the left is false.
            Expect("zero != 0 && a / zero > 1", 0, "short circuit avoids the divide");

            ModbusBridge.Core.Data.Expression.TryParse("a +", out _, out var incomplete);
            Check(incomplete is not null, "an incomplete expression is rejected, not thrown");
            ModbusBridge.Core.Data.Expression.TryParse("bogus(1)", out _, out var unknown);
            Check(unknown is not null, "an unknown function is rejected");

            ModbusBridge.Core.Data.Expression.TryParse("x.y + z", out var refs, out _);
            Check(refs is not null && refs.References.Count == 2 && refs.References.Contains("x.y"),
                  "references are collected for quality checks");
        }

        Section("Address base");
        var modicon = new ModbusServerConfig
        {
            AddressBase = 0,
            AddressBaseByArea = new Dictionary<ModbusArea, int>(Addressing.Modicon)
        };
        Check(modicon.BaseFor(ModbusArea.HoldingRegister) == 40001, "Modicon holding-register base is 40001");
        Check(modicon.BaseFor(ModbusArea.Coil) == 1, "Modicon coil base is 1");

        await engine.DisposeAsync();

        Console.WriteLine();
        if (_failures == 0)
        {
            Console.WriteLine("ALL CHECKS PASSED");
            return 0;
        }

        Console.WriteLine($"{_failures} CHECK(S) FAILED");
        return 1;
    }

    private static BridgeConfig BuildConfig()
    {
        var config = new BridgeConfig
        {
            General = new GeneralConfig { LogLevel = LogLevel.Info, GlobalStaleTimeoutMs = 0 },
            Simulation = new SimulationConfig
            {
                Enabled = true,
                VirtualPlcEnabled = true,
                VirtualPlcBindAddress = "127.0.0.1",
                VirtualPlcPort = VirtualPlcPort,
                GenerateTelemetry = true,
                Tags = new System.Collections.ObjectModel.ObservableCollection<SimulatedTagConfig>
                {
                    new() { Tag = "sim.speedKph", Waveform = "sine", Min = 0, Max = 280, PeriodSec = 20 },
                    new() { Tag = "sim.rpm", Waveform = "sawtooth", Min = 800, Max = 9000, PeriodSec = 6 }
                }
            }
        };

        // ---- The PLC side: point a device at the virtual PLC ----
        var plc = new ModbusClientConfig
        {
            Id = "plc",
            Name = "virtual PLC",
            Enabled = true,
            Host = "127.0.0.1",
            Port = VirtualPlcPort,
            UnitId = 1,
            TagPrefix = "plc.",
            AddressBase = 0,
            ResponseTimeoutMs = 1000
        };

        var digital = new ReadGroupConfig
        {
            Name = "digital inputs",
            Area = ModbusArea.DiscreteInput,
            StartAddress = 0,
            Count = 16,
            PollIntervalMs = 10
        };
        for (var i = 0; i < 16; i++)
            digital.Points.Add(new PointConfig { Tag = $"di.b{i:00}", Offset = i, DataType = PointDataType.Bool });

        // The same physical contact mapped a second time as normally-closed, to prove inversion.
        digital.Points.Add(new PointConfig
        {
            Tag = "di.b00_nc", Offset = 0, DataType = PointDataType.Bool, Invert = true
        });
        plc.ReadGroups.Add(digital);

        var analog = new ReadGroupConfig
        {
            Name = "analog inputs",
            Area = ModbusArea.InputRegister,
            StartAddress = 0,
            Count = 8,
            PollIntervalMs = 10
        };
        for (var i = 0; i < 4; i++)
        {
            analog.Points.Add(new PointConfig
            {
                Tag = $"ai.axis{i + 1}",
                Offset = i,
                DataType = PointDataType.Int16,
                Scale = new ScalingConfig { Gain = 1.0 / 27648.0, Min = -1, Max = 1 }
            });
        }
        plc.ReadGroups.Add(analog);

        // Telemetry pushed back into the PLC's holding registers.
        var toPlc = new WriteGroupConfig
        {
            Name = "telemetry to PLC",
            Area = ModbusArea.HoldingRegister,
            StartAddress = 100,
            Mode = WriteMode.OnChangeAndPeriodic,
            PeriodMs = 50
        };
        toPlc.Points.Add(new PointConfig
        {
            Tag = "sim.rpm",
            Offset = 0,
            DataType = PointDataType.Int16,
            Access = AccessMode.Write,
            Scale = new ScalingConfig { Gain = 27648.0 / 12000.0, Min = 0, Max = 12000 }
        });
        plc.WriteGroups.Add(toPlc);
        config.Clients.Add(plc);

        // ---- The HMI side: serve everything back out ----
        var server = new ModbusServerConfig
        {
            Id = "hmi",
            Name = "HMI server",
            Enabled = true,
            BindAddress = "127.0.0.1",
            Port = HmiServerPort,
            AddressBase = 0
        };

        var map = new ServerMapConfig { Name = "test map", UnitId = 1, AcceptAnyUnitId = true };

        var floats = new ServerBlockConfig
        {
            Name = "telemetry floats",
            Area = ModbusArea.HoldingRegister,
            StartAddress = 0,
            Size = 32
        };
        floats.Points.Add(new PointConfig { Tag = "sim.speedKph", Offset = 0, DataType = PointDataType.Float32 });
        floats.Points.Add(new PointConfig { Tag = "sim.rpm", Offset = 2, DataType = PointDataType.Float32 });
        floats.Points.Add(new PointConfig { Tag = "plc.ai.axis1", Offset = 4, DataType = PointDataType.Float32 });
        map.Blocks.Add(floats);

        var writable = new ServerBlockConfig
        {
            Name = "HMI setpoints",
            Area = ModbusArea.HoldingRegister,
            StartAddress = 200,
            Size = 16
        };
        writable.Points.Add(new PointConfig
        {
            Tag = "hmi.setpoint", Offset = 0, DataType = PointDataType.UInt16, Access = AccessMode.ReadWrite
        });
        map.Blocks.Add(writable);

        var bits = new ServerBlockConfig
        {
            Name = "PLC inputs mirrored",
            Area = ModbusArea.Coil,
            StartAddress = 0,
            Size = 16
        };
        for (var i = 0; i < 16; i++)
            bits.Points.Add(new PointConfig { Tag = $"plc.di.b{i:00}", Offset = i, DataType = PointDataType.Bool });
        map.Blocks.Add(bits);

        var hmiButtons = new ServerBlockConfig
        {
            Name = "HMI buttons",
            Area = ModbusArea.Coil,
            StartAddress = 100,
            Size = 16
        };
        for (var i = 0; i < 16; i++)
            hmiButtons.Points.Add(new PointConfig
            {
                Tag = $"hmi.btn{i + 1:00}", Offset = i, DataType = PointDataType.Bool, Access = AccessMode.ReadWrite
            });
        map.Blocks.Add(hmiButtons);

        server.Maps.Add(map);
        config.Servers.Add(server);

        // ---- vJoy output: two tags the end-to-end check forces and reads back through winmm ----
        var vjoyDevice = new VJoyDeviceConfig
        {
            DeviceId = 1,
            Enabled = true,
            UpdateIntervalMs = 2,
            ReleaseOnBadQuality = true
        };
        vjoyDevice.Buttons.Add(new VJoyButtonMapping
        {
            Tag = "vjoy.testButton", Button = 1, Mode = VJoyButtonMode.Momentary
        });
        vjoyDevice.Buttons.Add(new VJoyButtonMapping
        {
            Tag = "vjoy.testToggle", Button = 2, Mode = VJoyButtonMode.Toggle
        });
        vjoyDevice.Buttons.Add(new VJoyButtonMapping
        {
            Tag = "vjoy.testPulse", Button = 3, Mode = VJoyButtonMode.Pulse, PulseMs = 120
        });
        vjoyDevice.Buttons.Add(new VJoyButtonMapping
        {
            Tag = "vjoy.testNc", Button = 4, Mode = VJoyButtonMode.Momentary, Invert = true
        });
        // Shift layers: button 5 normally, button 6 while the modifier is held. The base mapping
        // for the same tag must stop driving button 5 the moment the layer takes over.
        vjoyDevice.Layers.Add(new VJoyShiftLayer
        {
            Name = "shift", ModifierTag = "vjoy.testShift", Priority = 1
        });
        vjoyDevice.Buttons.Add(new VJoyButtonMapping
        {
            Tag = "vjoy.testLayered", Button = 5, Mode = VJoyButtonMode.Momentary
        });
        vjoyDevice.Buttons.Add(new VJoyButtonMapping
        {
            Tag = "vjoy.testLayered", Button = 6, Mode = VJoyButtonMode.Momentary, Layer = "shift"
        });

        vjoyDevice.Axes.Add(new VJoyAxisMapping
        {
            Tag = "vjoy.testAxis", Axis = VJoyAxis.X, InputMin = 0, InputMax = 100
        });

        // A hat driven by four separate contacts, which is how an arcade stick is actually wired.
        // Kind is chosen from what the device reports: continuous and discrete are separate pools
        // in vJoy and a device configured for one has none of the other.
        var probe = VJoyDevice.Probe(vjoyDevice.DeviceId);
        vjoyDevice.Povs.Add(new VJoyPovMapping
        {
            Pov = 1,
            Source = VJoyPovSource.Contacts,
            Kind = probe is { ContinuousPovCount: > 0 } ? VJoyPovKind.Continuous : VJoyPovKind.Discrete,
            UpTag = "vjoy.testHatUp",
            RightTag = "vjoy.testHatRight",
            DownTag = "vjoy.testHatDown",
            LeftTag = "vjoy.testHatLeft"
        });

        config.VJoy.Enabled = true;
        config.VJoy.Devices.Add(vjoyDevice);

        // ---- SimHub telemetry ingest ----
        config.Telemetry.Enabled = true;
        config.Telemetry.BindAddress = "127.0.0.1";
        config.Telemetry.ListenPort = TelemetryPort;
        config.Telemetry.TagPrefix = "sim.";
        config.Telemetry.TimeoutMs = 1500;
        config.Telemetry.FeedbackIntervalMs = 50;

        foreach (var property in new[] { "SpeedKmh", "Rpms", "Gear", "ABSActive", "CarModel" })
            config.Telemetry.Subscriptions.Add(new TelemetrySubscription { Property = property });

        // The same property a second time, scaled - proves conversion happens on the way in.
        config.Telemetry.Subscriptions.Add(new TelemetrySubscription
        {
            Property = "SpeedKmh",
            Tag = "sim.speedMph",
            Scale = new ScalingConfig { Gain = 0.621371 },
            Units = "mph"
        });

        config.Telemetry.Feedback.Add(new TelemetryFeedback { Tag = "plc.di.b01", Name = "estop" });

        var problems = config.Validate();
        foreach (var problem in problems) Console.WriteLine($"  CONFIG PROBLEM: {problem}");

        return config;
    }

    private static void CheckCodec(PointDataType type, double value, WordOrder wordOrder, ByteOrder byteOrder)
    {
        var registers = new ushort[ValueCodec.RegisterCount(type)];
        ValueCodec.Encode(value, registers, type, wordOrder, byteOrder);
        var decoded = ValueCodec.Decode(registers, type, wordOrder, byteOrder);

        var tolerance = type == PointDataType.Float32 ? Math.Abs(value) * 1e-6 + 1e-6 : 1e-9;
        Check(Math.Abs(decoded - value) <= tolerance,
              $"{type} {wordOrder}/{byteOrder}: {value} -> [{string.Join(" ", registers.Select(r => r.ToString("X4")))}] -> {decoded}");
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                Check(true, what);
                return;
            }
            await Task.Delay(100);
        }
        Check(false, $"{what} (timed out after {timeout.TotalSeconds:0} s)");
    }

    /// <summary>An informational line that is neither a pass nor a fail (skips, environment facts).</summary>
    private static void Note(string text)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write("  ....  ");
        Console.ForegroundColor = previous;
        Console.WriteLine(text);
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"== {title} " + new string('=', Math.Max(0, 60 - title.Length)));
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) _failures++;
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = condition ? ConsoleColor.Green : ConsoleColor.Red;
        Console.Write(condition ? "  PASS  " : "  FAIL  ");
        Console.ForegroundColor = previous;
        Console.WriteLine(description);
    }
}
