using System.Net;
using System.Net.Sockets;

namespace ModbusBridge.Core.Modbus;

/// <summary>Matches a client address against a rule that is either a plain IP or CIDR notation.</summary>
public static class IpAllowList
{
    public static bool Matches(string rule, IPAddress address)
    {
        if (string.IsNullOrWhiteSpace(rule)) return false;
        rule = rule.Trim();

        if (rule == "*" || rule.Equals("any", StringComparison.OrdinalIgnoreCase)) return true;

        // Compare v4-mapped v6 addresses (::ffff:192.0.2.5) on their v4 form.
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        var slash = rule.IndexOf('/');
        if (slash < 0)
            return IPAddress.TryParse(rule, out var single) &&
                   Normalize(single).Equals(address);

        if (!IPAddress.TryParse(rule[..slash], out var network)) return false;
        if (!int.TryParse(rule[(slash + 1)..], out var prefixLength)) return false;

        network = Normalize(network);
        if (network.AddressFamily != address.AddressFamily) return false;

        var networkBytes = network.GetAddressBytes();
        var addressBytes = address.GetAddressBytes();
        if (prefixLength < 0 || prefixLength > networkBytes.Length * 8) return false;

        var fullBytes = prefixLength / 8;
        for (var i = 0; i < fullBytes; i++)
            if (networkBytes[i] != addressBytes[i]) return false;

        var remainingBits = prefixLength % 8;
        if (remainingBits == 0) return true;

        var mask = (byte)(0xFF << 8 - remainingBits);
        return (networkBytes[fullBytes] & mask) == (addressBytes[fullBytes] & mask);
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6
            ? address.MapToIPv4()
            : address;
}
