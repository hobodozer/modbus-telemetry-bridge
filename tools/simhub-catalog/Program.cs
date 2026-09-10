// Dumps the property catalog the SimHub plugin is offering.
//
// The bridge's GUI has a property browser, but picking subscription names for a config file
// needs the list as text. This binds the bridge's telemetry port, so the BRIDGE MUST BE STOPPED
// while it runs - the plugin sends to exactly one port.

using System.Net;
using System.Net.Sockets;
using ModbusBridge.Telemetry;

int port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 15600;
string filter = args.Length > 1 ? args[1] : null;

// --check <bridge.json> subscribes every configured property and reports which ones the
// plugin actually resolves. The schema it sends back lists exactly what it will stream, so a
// misspelt property shows up immediately - without needing the game to be running.
string checkConfig = null;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--check") { checkConfig = args[i + 1]; filter = null; }

using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
// Without this, the other end closing turns the next receive into an exception.
try { socket.Client.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null); } catch { }

// A full catalog is ~10 back-to-back datagrams of up to 8 KB, which overruns the default
// 64 KB receive buffer and silently loses one. Give it room and never sleep mid-burst.
socket.Client.ReceiveBufferSize = 4 << 20;

Console.WriteLine($"Listening on 127.0.0.1:{port}. Waiting for the plugin...");

var wanted = new List<string>();
var chunks = new Dictionary<int, List<TelemetryProperty>>();
int expected = -1;
var deadline = DateTime.UtcNow.AddSeconds(20);

while (DateTime.UtcNow < deadline)
{
    if (socket.Available == 0) { await Task.Delay(5); continue; }

    var remote = new IPEndPoint(IPAddress.Any, 0);
    var buffer = socket.Receive(ref remote);
    if (!TelemetryProtocol.TryReadHeader(buffer, buffer.Length, out var type)) continue;

    if (type == TelemetryMessageType.Hello)
    {
        TelemetryProtocol.ReadHello(buffer, buffer.Length, out var version, out var game, out var running);
        Console.WriteLine($"Plugin {version}, game '{game}', running={running}.");
        if (checkConfig != null)
        {
            if (wanted.Count == 0)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(checkConfig));
                foreach (var sub in doc.RootElement.GetProperty("telemetry").GetProperty("subscriptions").EnumerateArray())
                    wanted.Add(sub.GetProperty("property").GetString());
                Console.WriteLine($"Subscribing {wanted.Count} propertie(s) from {checkConfig}...");
            }
            var subscribe = TelemetryProtocol.BuildSubscribe(wanted);
            socket.Send(subscribe, subscribe.Length, remote);
        }
        else
        {
            var request = TelemetryProtocol.BuildRequestCatalog();
            socket.Send(request, request.Length, remote);
        }
    }
    else if (type == TelemetryMessageType.Schema && checkConfig != null)
    {
        var resolved = TelemetryProtocol.ReadSchema(buffer, buffer.Length, out _);
        var got = resolved.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Console.WriteLine("");
        Console.WriteLine($"plugin resolved {got.Count} of {wanted.Count} subscribed propertie(s):");
        foreach (var name in wanted)
            Console.WriteLine($"   {(got.Contains(name) ? "ok    " : "MISSING")} {name}");
        return got.Count == wanted.Count ? 0 : 1;
    }
    else if (type == TelemetryMessageType.Catalog)
    {
        var part = TelemetryProtocol.ReadCatalogChunk(buffer, buffer.Length, out var index, out var count);
        expected = count;
        chunks[index] = part;
        if (chunks.Count == expected) break;
    }
}

if (expected <= 0 || chunks.Count != expected)
{
    Console.WriteLine($"Did not receive a complete catalog ({chunks.Count}/{expected} chunks). " +
                      "Is SimHub running, and is the bridge stopped?");
    return 1;
}

var all = chunks.OrderBy(c => c.Key).SelectMany(c => c.Value).ToList();
Console.WriteLine($"{all.Count} properties.");
foreach (var property in all.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
{
    if (filter != null && property.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
    Console.WriteLine($"{property.Type,-8} {property.Name}");
}
return 0;
