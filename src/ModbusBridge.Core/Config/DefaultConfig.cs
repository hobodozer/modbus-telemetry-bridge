using System.Collections.ObjectModel;
using ModbusBridge.Core.Data;

namespace ModbusBridge.Core.Config;

/// <summary>
/// The configuration written on first run. It is a working example shaped around a Siemens
/// ET 200SP CPU running MB_SERVER, plus one HMI map - not a fixed assignment, just a starting point
/// that shows every construct the editor can produce.
/// </summary>
public static class DefaultConfig
{
    public static BridgeConfig Create()
    {
        var config = new BridgeConfig
        {
            General = new GeneralConfig
            {
                ProfileName = "default",
                LogLevel = LogLevel.Info,
                StartEnginesOnLaunch = true,
                GlobalStaleTimeoutMs = 5000
            }
        };

        config.Clients.Add(BuildExamplePlc());
        config.Servers.Add(BuildExampleHmiServer());
        config.Simulation = BuildSimulation();

        return config;
    }

    private static ModbusClientConfig BuildExamplePlc()
    {
        var plc = new ModbusClientConfig
        {
            Id = "plc1",
            Name = "ET 200SP",
            Enabled = false,                       // off until the address is real
            Host = "192.168.1.10",
            Port = 502,
            UnitId = 1,
            TagPrefix = "plc1.",
            // S7 is big-endian throughout, so a Siemens REAL lands on Float32/HighFirst directly.
            WordOrder = WordOrder.HighFirst,
            ByteOrder = ByteOrder.HighFirst,
            // Siemens MB_SERVER documentation numbers registers from 1.
            AddressBase = 1,
            ResponseTimeoutMs = 500,
            ReconnectDelayMs = 1000,
            MaxReconnectDelayMs = 15000
        };

        // Sample sizes only. Group count, point count, addresses and types are all driven by
        // config - add or delete rows in the editor and the engine follows. Nothing downstream
        // (including the vJoy feeder) assumes any particular number of buttons or axes.
        const int SampleDigitalInputs = 64;
        const int SampleAnalogInputs = 4;

        var digitalIn = new ReadGroupConfig
        {
            Name = "digital inputs",
            Area = ModbusArea.Coil,
            StartAddress = 1,
            Count = SampleDigitalInputs,
            PollIntervalMs = 10
        };
        for (var i = 0; i < SampleDigitalInputs; i++)
        {
            digitalIn.Points.Add(new PointConfig
            {
                Tag = $"di.btn{i + 1:00}",
                Offset = i,
                DataType = PointDataType.Bool,
                Access = AccessMode.Read,
                Invert = false,                     // set true for a normally-closed contact
                Description = $"Panel button {i + 1}"
            });
        }
        plc.ReadGroups.Add(digitalIn);

        // Four analog inputs, raw 0..27648 which is the Siemens full-scale count for a +/-10 V / 0..20 mA channel.
        var analogIn = new ReadGroupConfig
        {
            Name = "analog inputs",
            Area = ModbusArea.HoldingRegister,
            StartAddress = 1,
            Count = SampleAnalogInputs * 2,
            PollIntervalMs = 10
        };
        for (var i = 0; i < SampleAnalogInputs; i++)
        {
            analogIn.Points.Add(new PointConfig
            {
                Tag = $"ai.axis{i + 1}",
                Offset = i,
                DataType = PointDataType.Int16,
                Access = AccessMode.Read,
                Scale = new ScalingConfig
                {
                    Gain = 1.0 / 27648.0,           // Siemens nominal full scale
                    Offset = 0,
                    Min = 0,
                    Max = 1,
                    Deadband = 0.001,
                    DisplayDecimals = 3
                },
                Units = "0..1",
                Description = $"Analog axis {i + 1}"
            });
        }
        plc.ReadGroups.Add(analogIn);

        // Telemetry pushed back to the PLC so its analog outputs can drive real gauges.
        var analogOut = new WriteGroupConfig
        {
            Name = "telemetry to PLC analog outputs",
            Area = ModbusArea.HoldingRegister,
            StartAddress = 101,
            Mode = WriteMode.OnChangeAndPeriodic,
            PeriodMs = 20,
            UseMultipleWrite = true
        };
        analogOut.Points.Add(new PointConfig
        {
            Tag = "sim.rpm",
            Offset = 0,
            DataType = PointDataType.Int16,
            Access = AccessMode.Write,
            Scale = new ScalingConfig { Gain = 27648.0 / 12000.0, Min = 0, Max = 12000, Deadband = 5 },
            Units = "rpm",
            Description = "Engine RPM scaled to full-scale analog output"
        });
        analogOut.Points.Add(new PointConfig
        {
            Tag = "sim.speedKph",
            Offset = 1,
            DataType = PointDataType.Int16,
            Access = AccessMode.Write,
            Scale = new ScalingConfig { Gain = 27648.0 / 400.0, Min = 0, Max = 400, Deadband = 0.5 },
            Units = "km/h",
            Description = "Road speed scaled to full-scale analog output"
        });
        analogOut.Points.Add(new PointConfig
        {
            Tag = "sim.fuelPercent",
            Offset = 2,
            DataType = PointDataType.Int16,
            Access = AccessMode.Write,
            Scale = new ScalingConfig { Gain = 27648.0 / 100.0, Min = 0, Max = 100, Deadband = 0.2 },
            Units = "%",
            Description = "Fuel level scaled to full-scale analog output"
        });
        plc.WriteGroups.Add(analogOut);

        plc.Watchdog = new WatchdogConfig
        {
            Enabled = false,
            WriteArea = ModbusArea.HoldingRegister,
            WriteAddress = 200,
            ReadArea = ModbusArea.HoldingRegister,
            ReadAddress = 201,
            IntervalMs = 250,
            TimeoutMs = 1500,
            HealthTag = "link.healthy"
        };

        return plc;
    }

    private static ModbusServerConfig BuildExampleHmiServer()
    {
        var server = new ModbusServerConfig
        {
            Id = "hmi",
            Name = "HMI server",
            Enabled = true,
            BindAddress = "0.0.0.0",
            Port = 502,
            MaxClients = 16,
            AddressBase = 1,
            WordOrder = WordOrder.HighFirst,
            ByteOrder = ByteOrder.HighFirst,
            IdleTimeoutSec = 120
        };

        var map = new ServerMapConfig
        {
            Name = "telemetry",
            UnitId = 1,
            AcceptAnyUnitId = true
        };

        // Float telemetry, two registers per value.
        var floats = new ServerBlockConfig
        {
            Name = "telemetry floats",
            Area = ModbusArea.HoldingRegister,
            StartAddress = 1,
            Size = 64,
            StaleBehavior = StaleBehavior.Failsafe,
            StaleTimeoutMs = 2000
        };
        var floatTags = new (string Tag, string Units, string Description)[]
        {
            ("sim.speedKph", "km/h", "Road speed"),
            ("sim.rpm", "rpm", "Engine RPM"),
            ("sim.fuelPercent", "%", "Fuel remaining"),
            ("sim.waterTempC", "degC", "Water temperature"),
            ("sim.oilTempC", "degC", "Oil temperature"),
            ("sim.oilPressureBar", "bar", "Oil pressure"),
            ("sim.turboBar", "bar", "Turbo boost"),
            ("sim.brakePercent", "%", "Brake input"),
            ("sim.throttlePercent", "%", "Throttle input"),
            ("sim.clutchPercent", "%", "Clutch input"),
            ("sim.steerAngleDeg", "deg", "Steering angle"),
            ("sim.lapTimeSec", "s", "Current lap time")
        };
        for (var i = 0; i < floatTags.Length; i++)
        {
            floats.Points.Add(new PointConfig
            {
                Tag = floatTags[i].Tag,
                Offset = i * 2,
                DataType = PointDataType.Float32,
                Access = AccessMode.Read,
                Units = floatTags[i].Units,
                Description = floatTags[i].Description
            });
        }
        map.Blocks.Add(floats);

        // Integer telemetry, one register per value.
        var integers = new ServerBlockConfig
        {
            Name = "telemetry integers",
            Area = ModbusArea.HoldingRegister,
            StartAddress = 101,
            Size = 32,
            StaleBehavior = StaleBehavior.Failsafe,
            StaleTimeoutMs = 2000
        };
        var intTags = new (string Tag, string Units, string Description)[]
        {
            ("sim.gear", "", "Selected gear, 0 = neutral"),
            ("sim.position", "", "Race position"),
            ("sim.lap", "", "Current lap"),
            ("sim.totalLaps", "", "Total laps"),
            ("sim.flag", "", "Track flag code")
        };
        for (var i = 0; i < intTags.Length; i++)
        {
            integers.Points.Add(new PointConfig
            {
                Tag = intTags[i].Tag,
                Offset = i,
                DataType = PointDataType.Int16,
                Access = AccessMode.Read,
                Units = intTags[i].Units,
                Description = intTags[i].Description
            });
        }
        map.Blocks.Add(integers);

        // Status bits the HMI can light up, packed 16 to a register as well as exposed as coils.
        var status = new ServerBlockConfig
        {
            Name = "status bits",
            Area = ModbusArea.Coil,
            StartAddress = 1,
            Size = 32,
            StaleBehavior = StaleBehavior.Failsafe
        };
        var bitTags = new (string Tag, string Description)[]
        {
            ("sim.engineRunning", "Engine running"),
            ("sim.pitLimiter", "Pit limiter active"),
            ("sim.absActive", "ABS engaging"),
            ("sim.tcActive", "Traction control engaging"),
            ("sim.drsAvailable", "DRS available"),
            ("sim.drsActive", "DRS open"),
            ("sim.headlights", "Headlights on"),
            ("sim.inPit", "In pit lane"),
            ("plc1.link.healthy", "PLC link healthy"),
            ("bridge.simhubConnected", "SimHub feeding telemetry")
        };
        for (var i = 0; i < bitTags.Length; i++)
        {
            status.Points.Add(new PointConfig
            {
                Tag = bitTags[i].Tag,
                Offset = i,
                DataType = PointDataType.Bool,
                Access = AccessMode.Read,
                Description = bitTags[i].Description
            });
        }
        map.Blocks.Add(status);

        // Coils the HMI writes - its on-screen buttons become inputs everywhere else in the bridge.
        var hmiButtons = new ServerBlockConfig
        {
            Name = "HMI buttons",
            Area = ModbusArea.Coil,
            StartAddress = 101,
            Size = 32
        };
        for (var i = 0; i < 16; i++)
        {
            hmiButtons.Points.Add(new PointConfig
            {
                Tag = $"hmi.btn{i + 1:00}",
                Offset = i,
                DataType = PointDataType.Bool,
                Access = AccessMode.ReadWrite,
                Description = $"HMI soft button {i + 1}"
            });
        }
        map.Blocks.Add(hmiButtons);

        server.Maps.Add(map);
        return server;
    }

    private static SimulationConfig BuildSimulation() => new()
    {
        Enabled = false,
        VirtualPlcEnabled = true,
        VirtualPlcBindAddress = "127.0.0.1",
        VirtualPlcPort = 15020,
        GenerateTelemetry = true,
        Tags = new ObservableCollection<SimulatedTagConfig>
        {
            new() { Tag = "sim.speedKph", Waveform = "sine", Min = 0, Max = 280, PeriodSec = 24 },
            new() { Tag = "sim.rpm", Waveform = "sawtooth", Min = 800, Max = 9000, PeriodSec = 6 },
            new() { Tag = "sim.fuelPercent", Waveform = "ramp", Min = 0, Max = 100, PeriodSec = 300 },
            new() { Tag = "sim.waterTempC", Waveform = "sine", Min = 70, Max = 110, PeriodSec = 60 },
            new() { Tag = "sim.oilTempC", Waveform = "sine", Min = 80, Max = 130, PeriodSec = 75 },
            new() { Tag = "sim.oilPressureBar", Waveform = "sine", Min = 1, Max = 6, PeriodSec = 12 },
            new() { Tag = "sim.turboBar", Waveform = "triangle", Min = 0, Max = 2, PeriodSec = 8 },
            new() { Tag = "sim.throttlePercent", Waveform = "triangle", Min = 0, Max = 100, PeriodSec = 5 },
            new() { Tag = "sim.brakePercent", Waveform = "triangle", Min = 0, Max = 100, PeriodSec = 7 },
            new() { Tag = "sim.clutchPercent", Waveform = "square", Min = 0, Max = 100, PeriodSec = 11 },
            new() { Tag = "sim.steerAngleDeg", Waveform = "sine", Min = -450, Max = 450, PeriodSec = 9 },
            new() { Tag = "sim.gear", Waveform = "sawtooth", Min = 0, Max = 7, PeriodSec = 14 },
            new() { Tag = "sim.lapTimeSec", Waveform = "ramp", Min = 0, Max = 120, PeriodSec = 120 },
            new() { Tag = "sim.engineRunning", Waveform = "constant", Min = 1, Max = 1 },
            new() { Tag = "sim.pitLimiter", Waveform = "square", Min = 0, Max = 1, PeriodSec = 20 },
            new() { Tag = "sim.absActive", Waveform = "square", Min = 0, Max = 1, PeriodSec = 3 },
            new() { Tag = "sim.tcActive", Waveform = "square", Min = 0, Max = 1, PeriodSec = 4 }
        }
    };
}
