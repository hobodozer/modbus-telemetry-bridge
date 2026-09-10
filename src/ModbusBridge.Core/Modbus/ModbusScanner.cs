using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace ModbusBridge.Core.Modbus;

/// <summary>What a scan found at one address.</summary>
public sealed class ScanResult
{
    public required IPAddress Address { get; init; }
    public required int Port { get; init; }

    /// <summary>The TCP connect succeeded. Something is listening, not necessarily Modbus.</summary>
    public bool PortOpen { get; init; }

    /// <summary>A well-formed Modbus reply came back - including an exception reply, which still proves it.</summary>
    public bool SpeaksModbus { get; init; }

    /// <summary>Unit ids that answered. Only populated when unit scanning is requested.</summary>
    public List<byte> UnitIds { get; } = new();

    /// <summary>Vendor / product / revision from FC 43/14, when the device implements it. Many do not.</summary>
    public string? Identification { get; init; }

    /// <summary>Areas that returned data rather than an exception, e.g. "holding, input".</summary>
    public string? ReadableAreas { get; init; }

    public double ConnectMs { get; init; }
    public string? Note { get; init; }

    public override string ToString()
    {
        var what = SpeaksModbus ? "Modbus" : PortOpen ? "open, not Modbus" : "closed";
        var extra = Identification is null ? "" : $"  {Identification}";
        return $"{Address}:{Port}  {what}{extra}";
    }
}

/// <summary>
/// Finds Modbus TCP devices on a network. Modbus has no discovery of its own - a client is told
/// its address map by configuration and can never ask for one - so the most that is possible is
/// to find what listens and confirm it speaks the protocol.
///
/// Strictly read-only: it never issues a write function code.
/// </summary>
public static class ModbusScanner
{
    /// <summary>Every IPv4 address on the local subnets, excluding this machine's own.</summary>
    public static List<IPAddress> LocalSubnetTargets(int maxHosts = 1024)
    {
        var targets = new List<IPAddress>();
        var mine = new HashSet<string>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (unicast.IPv4Mask is null) continue;
                // Link-local addresses mean no DHCP answered; scanning them finds nothing.
                if (unicast.Address.ToString().StartsWith("169.254.")) continue;

                mine.Add(unicast.Address.ToString());
                foreach (var address in Enumerate(unicast.Address, unicast.IPv4Mask, maxHosts))
                    targets.Add(address);
            }
        }

        return targets.Where(a => !mine.Contains(a.ToString()))
                      .DistinctBy(a => a.ToString())
                      .ToList();
    }

    /// <summary>Expands "192.0.2.0/24" into host addresses, network and broadcast excluded.</summary>
    public static List<IPAddress> ParseCidr(string cidr, int maxHosts = 65536)
    {
        var parts = cidr.Split('/');
        var address = IPAddress.Parse(parts[0].Trim());
        if (parts.Length == 1) return new List<IPAddress> { address };

        var prefix = int.Parse(parts[1].Trim());
        if (prefix is < 0 or > 32) throw new FormatException($"'{cidr}' has an invalid prefix length.");

        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        return Enumerate(address, new IPAddress(BitConverter.GetBytes(ReverseBytes(mask))), maxHosts);
    }

    private static List<IPAddress> Enumerate(IPAddress address, IPAddress mask, int maxHosts)
    {
        var addressBits = ReverseBytes(BitConverter.ToUInt32(address.GetAddressBytes()));
        var maskBits = ReverseBytes(BitConverter.ToUInt32(mask.GetAddressBytes()));

        var network = addressBits & maskBits;
        var broadcast = network | ~maskBits;

        var results = new List<IPAddress>();
        // A /31 or /32 has no network/broadcast pair to skip.
        var first = broadcast - network >= 2 ? network + 1 : network;
        var last = broadcast - network >= 2 ? broadcast - 1 : broadcast;

        for (var host = first; host <= last && results.Count < maxHosts; host++)
            results.Add(new IPAddress(BitConverter.GetBytes(ReverseBytes(host))));

        return results;
    }

    private static uint ReverseBytes(uint value) =>
        (value >> 24) | ((value >> 8) & 0xFF00) | ((value << 8) & 0xFF0000) | (value << 24);

    /// <summary>
    /// Scans addresses for a Modbus TCP server. Runs a bounded number of probes at once so a /24
    /// finishes quickly without flooding the network or exhausting sockets.
    /// </summary>
    public static async Task<List<ScanResult>> ScanAsync(
        IEnumerable<IPAddress> targets,
        int port = 502,
        int timeoutMs = 400,
        int concurrency = 64,
        bool scanUnitIds = false,
        IProgress<ScanResult>? progress = null,
        CancellationToken ct = default)
    {
        var gate = new SemaphoreSlim(Math.Max(1, concurrency));
        var results = new List<ScanResult>();
        var sync = new object();

        var tasks = targets.Select(async address =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var result = await ProbeAsync(address, port, timeoutMs, scanUnitIds, ct).ConfigureAwait(false);
                if (result is null) return;

                lock (sync) results.Add(result);
                progress?.Report(result);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        return results.OrderBy(r => ReverseBytes(BitConverter.ToUInt32(r.Address.GetAddressBytes())))
                      .ToList();
    }

    /// <summary>Probes one address. Returns null when nothing is listening, so closed ports stay quiet.</summary>
    public static async Task<ScanResult?> ProbeAsync(IPAddress address, int port, int timeoutMs,
                                                     bool scanUnitIds, CancellationToken ct)
    {
        using var client = new TcpClient();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            await client.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            return null;    // refused, unreachable or timed out - not interesting
        }

        var connectMs = (System.Diagnostics.Stopwatch.GetTimestamp() - started)
                        * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        var stream = client.GetStream();
        stream.ReadTimeout = timeoutMs;
        stream.WriteTimeout = timeoutMs;

        var units = new List<byte>();
        string? identification = null;
        var areas = new List<string>();
        var speaks = false;

        // Unit 1 first: it is the common default and the cheapest question to ask.
        var candidates = scanUnitIds ? Enumerable.Range(1, 247).Select(i => (byte)i) : new byte[] { 1 };

        foreach (var unit in candidates)
        {
            var reply = await RequestAsync(stream, unit, 3, 0, 1, ct).ConfigureAwait(false);
            if (reply is null) continue;

            speaks = true;
            units.Add(unit);
            if (!scanUnitIds) break;
        }

        if (speaks && !scanUnitIds)
        {
            foreach (var (code, name) in new[] { (3, "holding"), (4, "input"), (1, "coil"), (2, "discrete") })
            {
                var reply = await RequestAsync(stream, units[0], (byte)code, 0, 1, ct).ConfigureAwait(false);
                // A non-exception reply means the area is actually mapped, not merely supported.
                if (reply is { Length: > 8 } && reply[7] < 0x80) areas.Add(name);
            }

            identification = await ReadIdentificationAsync(stream, units[0], ct).ConfigureAwait(false);
        }

        return new ScanResult
        {
            Address = address,
            Port = port,
            PortOpen = true,
            SpeaksModbus = speaks,
            Identification = identification,
            ReadableAreas = areas.Count > 0 ? string.Join(", ", areas) : null,
            ConnectMs = connectMs,
            Note = speaks ? null : "listening, but no Modbus reply"
        }.With(units);
    }

    private static ScanResult With(this ScanResult result, List<byte> units)
    {
        result.UnitIds.AddRange(units);
        return result;
    }

    /// <summary>Sends one read request and returns the raw reply, or null if nothing usable came back.</summary>
    private static async Task<byte[]?> RequestAsync(NetworkStream stream, byte unit, byte function,
                                                    int address, int count, CancellationToken ct)
    {
        var request = new byte[]
        {
            0, 1, 0, 0, 0, 6, unit, function,
            (byte)(address >> 8), (byte)address, (byte)(count >> 8), (byte)count
        };

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(stream.ReadTimeout);

            await stream.WriteAsync(request, timeout.Token).ConfigureAwait(false);

            var buffer = new byte[260];
            var read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);

            // Shortest valid reply is the 7-byte MBAP header plus a function code.
            if (read < 8) return null;
            // Protocol identifier 0 is what makes this Modbus rather than some other listener.
            if (buffer[2] != 0 || buffer[3] != 0) return null;
            return buffer[..read];
        }
        catch
        {
            return null;
        }
    }

    /// <summary>FC 43/14, Read Device Identification. Widely unimplemented, so absence proves nothing.</summary>
    private static async Task<string?> ReadIdentificationAsync(NetworkStream stream, byte unit,
                                                               CancellationToken ct)
    {
        var request = new byte[] { 0, 1, 0, 0, 0, 5, unit, 0x2B, 0x0E, 0x01, 0x00 };

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(stream.ReadTimeout);

            await stream.WriteAsync(request, timeout.Token).ConfigureAwait(false);

            var buffer = new byte[260];
            var read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            if (read < 10 || buffer[7] >= 0x80) return null;

            // MBAP(7) + function + MEI type + conformity + more + next + object count
            var offset = 7 + 6;
            if (offset >= read) return null;

            var objects = buffer[offset++];
            var parts = new List<string>();

            for (var i = 0; i < objects && offset + 2 <= read; i++)
            {
                offset++;                       // object id
                var length = buffer[offset++];
                if (offset + length > read) break;
                parts.Add(Encoding.ASCII.GetString(buffer, offset, length));
                offset += length;
            }

            return parts.Count > 0 ? string.Join(" / ", parts) : null;
        }
        catch
        {
            return null;
        }
    }
}
