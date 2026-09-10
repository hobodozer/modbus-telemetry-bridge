using ModbusBridge.Core.Config;
using ModbusBridge.Core.Data;
using ModbusBridge.Core.Diagnostics;
using ModbusBridge.Core.Engine;

namespace ModbusBridge.Cli;

/// <summary>
/// Headless host for the bridge.
///
/// The WPF app is the only genuinely Windows-bound part of this project, so it was also the only
/// way to run the engine at all - which meant "runs on Linux" was untestable no matter what the
/// libraries targeted. This is the same <see cref="BridgeEngine"/> with a console around it.
///
/// It is not a reduced version: the engine, the Modbus client and server, vJoy, the telemetry
/// ingest and the tag bus are identical. What differs is what the host machine can provide - vJoy
/// and the PDH counters are Windows-only and report themselves unavailable elsewhere rather than
/// failing the run.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Any(a => a is "--help" or "-h" or "/?"))
        {
            Usage();
            return 0;
        }

        var dataDirectory = ArgumentValue(args, "--data")
                            ?? Environment.GetEnvironmentVariable("MODBUSBRIDGE_DATA")
                            ?? Path.Combine(AppContext.BaseDirectory, "rig");
        dataDirectory = Path.GetFullPath(dataDirectory);

        var configPath = ArgumentValue(args, "--config")
                         ?? Path.Combine(dataDirectory, "config", "bridge.json");

        Paths.DataDirectory = dataDirectory;
        Log.LogDirectory = Path.Combine(dataDirectory, "logs");
        Log.WriteToFile = true;
        Log.MinimumLevel = args.Any(a => a is "--verbose" or "-v") ? LogLevel.Debug : LogLevel.Info;
        Log.Entry += entry => Console.WriteLine($"[{entry.Level,-5}] {entry.Source}: {entry.Message}");

        Console.WriteLine($"Modbus Telemetry Bridge (headless) on {Environment.OSVersion}");
        Console.WriteLine($"  data   : {dataDirectory}");
        Console.WriteLine($"  config : {configPath}");
        Console.WriteLine();

        var service = new ConfigService(configPath);

        BridgeConfig config;
        if (service.Exists)
        {
            try
            {
                config = service.Load();
            }
            catch (Exception ex)
            {
                // No dialog to fall back on here, and silently substituting defaults would start a
                // bridge that talks to the wrong devices. Refuse instead.
                Console.Error.WriteLine($"Could not load {configPath}: {ex.Message}");
                Console.Error.WriteLine("Fix the file, or pass --config to point somewhere else.");
                return 2;
            }
        }
        else if (args.Any(a => a is "--init"))
        {
            config = DefaultConfig.Create();
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            service.Save(config);
            Console.WriteLine($"Wrote a default configuration to {configPath}");
        }
        else
        {
            Console.Error.WriteLine($"No configuration at {configPath}");
            Console.Error.WriteLine("Pass --init to write a default one, or --config to point elsewhere.");
            return 2;
        }

        var problems = config.Validate();
        foreach (var problem in problems) Console.WriteLine($"  CONFIG: {problem}");
        if (problems.Count > 0 && !args.Any(a => a is "--force"))
        {
            Console.Error.WriteLine($"{problems.Count} configuration problem(s). Pass --force to start anyway.");
            return 3;
        }

        await using var engine = new BridgeEngine(config);

        // Ctrl+C, and SIGTERM from a container or systemd. Both have to release vJoy and lift any
        // held key: an output left asserted outlives the process that asserted it.
        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stopping.Cancel();

        await engine.StartAsync();
        Console.WriteLine();
        Console.WriteLine("Running. Ctrl+C to stop.");

        try
        {
            await Task.Delay(Timeout.Infinite, stopping.Token);
        }
        catch (OperationCanceledException)
        {
            // The expected way out.
        }

        Console.WriteLine();
        Console.WriteLine("Stopping...");
        await engine.StopAsync();
        Console.WriteLine("Stopped.");
        return 0;
    }

    private static string? ArgumentValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static void Usage()
    {
        Console.WriteLine("""
            Modbus Telemetry Bridge - headless host

              modbusbridge [--data <dir>] [--config <file>] [--init] [--force] [--verbose]

              --data <dir>     data directory for config, logs and recordings.
                               Defaults to $MODBUSBRIDGE_DATA, then <exe>/rig.
              --config <file>  configuration file. Defaults to <data>/config/bridge.json.
              --init           write a default configuration if none exists.
              --force          start even if validation reports problems.
              --verbose        debug-level logging.

            The Windows GUI is a separate executable and is not required. vJoy output and the
            PDH host counters are Windows-only; elsewhere they report themselves unavailable
            and the rest of the bridge runs normally.
            """);
    }
}
