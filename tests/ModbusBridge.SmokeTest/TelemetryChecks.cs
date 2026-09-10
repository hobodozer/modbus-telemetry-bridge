using ModbusBridge.Core.Data;
using ModbusBridge.Core.Engine;
using ModbusBridge.Core.Tags;
using ModbusBridge.Telemetry;

namespace ModbusBridge.SmokeTest;

/// <summary>
/// Exercises the whole SimHub path except SimHub itself: the handshake, the catalog, the property
/// browser feed, scaling, string properties, the offline timeout, and the reverse direction that
/// puts PLC contacts back into SimHub.
/// </summary>
internal static class TelemetryChecks
{
    public static async Task RunAsync(BridgeEngine engine, int listenPort,
                                      Action<bool, string> check, Action<string> section, Action<string> note)
    {
        section("SimHub telemetry (simulated plugin)");

        var ingest = engine.Telemetry;
        if (ingest is null)
        {
            check(false, "the engine started no telemetry ingest server");
            return;
        }

        check(ingest.IsRunning, $"ingest listening on port {listenPort}");

        using var plugin = new FakeSimHubPlugin(listenPort);

        // A realistic-looking catalog, including the properties the bridge subscribes to.
        plugin.Catalog.Add(new TelemetryProperty("SpeedKmh", TelemetryValueType.Number));
        plugin.Catalog.Add(new TelemetryProperty("Rpms", TelemetryValueType.Number));
        plugin.Catalog.Add(new TelemetryProperty("Gear", TelemetryValueType.Number));
        plugin.Catalog.Add(new TelemetryProperty("Fuel", TelemetryValueType.Number));
        plugin.Catalog.Add(new TelemetryProperty("ABSActive", TelemetryValueType.Bool));
        plugin.Catalog.Add(new TelemetryProperty("CarModel", TelemetryValueType.Text));
        for (var i = 0; i < 900; i++)
            plugin.Catalog.Add(new TelemetryProperty($"DataCorePlugin.GameData.Filler{i:000}", TelemetryValueType.Number));

        // ---- Handshake ----
        plugin.SendHello();

        var subscribeTask = plugin.WaitForSubscription();
        var completed = await Task.WhenAny(subscribeTask, Task.Delay(3000));
        if (completed != subscribeTask)
        {
            check(false, "bridge did not send a subscription within 3 s of Hello");
            return;
        }

        var subscribed = await subscribeTask;
        check(subscribed.Count > 0, $"bridge subscribed to {subscribed.Count} propertie(s): " +
                                    string.Join(", ", subscribed.Take(6)));
        check(subscribed.Contains("SpeedKmh"), "subscription includes SpeedKmh");

        var catalogTask = plugin.WaitForCatalogRequest();
        check(await Task.WhenAny(catalogTask, Task.Delay(2000)) == catalogTask,
              "bridge asked for a property catalog");

        // ---- Catalog: must survive being split across datagrams ----
        plugin.SendCatalog();
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (ingest.Catalog.Count < plugin.Catalog.Count && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        check(ingest.Catalog.Count == plugin.Catalog.Count,
              $"reassembled a {plugin.Catalog.Count}-property catalog from chunks " +
              $"(got {ingest.Catalog.Count})");
        check(ingest.Catalog.Any(p => p.Name == "CarModel" && p.Type == TelemetryValueType.Text),
              "catalog preserved the Text type of CarModel");

        // ---- Streaming ----
        plugin.SendSchema(subscribed);
        await Task.Delay(100);

        var numbers = new Dictionary<string, double>
        {
            ["SpeedKmh"] = 212.5,
            ["Rpms"] = 7400,
            ["Gear"] = 4,
            ["ABSActive"] = 1
        };
        var texts = new Dictionary<string, string> { ["CarModel"] = "Formula Hybrid 2023" };

        for (var i = 0; i < 12; i++)
        {
            plugin.SendData(numbers, texts);
            await Task.Delay(16);
        }
        await Task.Delay(150);

        check(ingest.Statistics.Frames >= 10,
              $"ingest received {ingest.Statistics.Frames} frame(s)");
        check(ingest.IsConnected, "ingest reports the plugin connected");

        var speed = engine.Tags.Find("sim.SpeedKmh");
        check(speed is not null, "tag 'sim.SpeedKmh' was auto-created from the property name");
        check(speed is not null && Math.Abs(speed.Value.Number - 212.5) < 0.001,
              $"SpeedKmh 212.5 reached the tag bus as {speed?.Value.Number}");
        check(speed is not null && speed.Value.Quality == TagQuality.Good, "telemetry tag quality is Good");

        // Scaling is applied on the way in, so an HMI can be handed mph without any PLC maths.
        var speedMph = engine.Tags.Find("sim.speedMph");
        check(speedMph is not null && Math.Abs(speedMph.Value.Number - 212.5 * 0.621371) < 0.01,
              $"scaled subscription converted 212.5 km/h to {speedMph?.Value.Number:0.00} mph");

        var carModel = engine.Tags.Find("sim.CarModel");
        check(carModel?.Value.Text == "Formula Hybrid 2023",
              $"string property arrived intact: '{carModel?.Value.Text}'");

        var absTag = engine.Tags.Find("sim.ABSActive");
        check(absTag is not null && absTag.Value.Bool, "bool property arrived as true");

        var gameTag = engine.Tags.Find("bridge.simhubGame");
        check(gameTag?.Value.Text == "Test Game", $"game name tag reads '{gameTag?.Value.Text}'");

        var connectedTag = engine.Tags.Find("bridge.simhubConnected");
        check(connectedTag is not null && connectedTag.Value.Bool, "connected tag is true while streaming");

        note($"frame interval {ingest.Statistics.IntervalMs:0.0} ms " +
             $"(worst {ingest.Statistics.MaxIntervalMs:0.0} ms), " +
             $"one-way latency {ingest.Statistics.LatencyMs:0.0} ms, " +
             $"{ingest.Statistics.Dropped} dropped");

        // ---- Reverse direction: bridge tags back into SimHub ----
        var contact = engine.Tags.GetOrAdd("plc.di.b01");
        contact.Force(TagValue.Good(true));
        await Task.Delay(300);

        var feedback = plugin.Feedback;
        check(feedback.Count > 0, $"plugin received {feedback.Count} fed-back value(s)");
        check(feedback.Any(f => f.Name == "estop" && f.Value != 0),
              "PLC contact reached the plugin as 'estop' = 1 " +
              $"(got {string.Join(", ", feedback.Select(f => $"{f.Name}={f.Value}"))})");

        contact.Force(TagValue.Good(false));
        await Task.Delay(300);
        check(plugin.Feedback.Any(f => f.Name == "estop" && f.Value == 0),
              "releasing the contact fed back as 0");
        contact.Unforce();

        // ---- Timeout: a closed game must not leave stale numbers looking live ----
        note("stopping the simulated plugin to check the offline timeout...");
        plugin.Dispose();
        await Task.Delay(2600);

        check(!ingest.IsConnected, "ingest reports disconnected once frames stop");
        check(connectedTag is not null && !connectedTag.Value.Bool, "connected tag went false on timeout");
        var speedAfter = engine.Tags.Find("sim.SpeedKmh");
        check(speedAfter is not null && speedAfter.Value.Quality != TagQuality.Good,
              $"telemetry tags no longer read Good after the timeout (quality {speedAfter?.Value.Quality})");
    }
}
