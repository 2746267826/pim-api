using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Pim.Api.Infrastructure.Ops;

public sealed class OpsKeyValidator
{
    private readonly string[] _keys;
    private readonly List<(IPAddress Network, int Prefix)> _cidrs;

    public OpsKeyValidator(string? configured, string? cidrs)
    {
        _keys = (configured ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _cidrs = ParseCidrs(cidrs);
    }

    public bool HasKeys => _keys.Length > 0;

    public bool IsValid(string? provided)
    {
        if (string.IsNullOrWhiteSpace(provided) || _keys.Length == 0) return false;
        var p = Encoding.UTF8.GetBytes(provided.Trim());
        foreach (var k in _keys)
        {
            var kb = Encoding.UTF8.GetBytes(k);
            if (p.Length == kb.Length && CryptographicOperations.FixedTimeEquals(p, kb)) return true;
        }
        return false;
    }

    public bool IsIpAllowed(string? ip)
    {
        if (_cidrs.Count == 0) return true;
        if (!IPAddress.TryParse(ip, out var addr)) return false;
        foreach (var (net, prefix) in _cidrs)
        {
            if (IsInRange(addr, net, prefix)) return true;
        }
        return false;
    }

    private static List<(IPAddress, int)> ParseCidrs(string? s)
    {
        var list = new List<(IPAddress, int)>();
        if (string.IsNullOrWhiteSpace(s)) return list;
        var failures = new List<string>();
        foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var slash = part.IndexOf('/');
            if (slash < 0)
            {
                if (IPAddress.TryParse(part, out var single))
                {
                    var prefix = single.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
                    list.Add((single, prefix));
                }
                else
                {
                    failures.Add(part);
                }
                continue;
            }
            var ipPart = part[..slash].Trim();
            var prefixPart = part[(slash + 1)..].Trim();
            if (!IPAddress.TryParse(ipPart, out var net))
            {
                failures.Add(part);
                continue;
            }
            if (!int.TryParse(prefixPart, out var parsedPrefix))
            {
                failures.Add(part);
                continue;
            }
            var maxPrefix = net.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
            if (parsedPrefix < 0 || parsedPrefix > maxPrefix)
            {
                failures.Add(part);
                continue;
            }
            list.Add((net, parsedPrefix));
        }
        if (failures.Count > 0)
            throw new OptionsValidationException(nameof(OpsOptions), typeof(OpsOptions), new[] { $"Invalid PIM_OPS_ALLOWED_CIDRS entries: {string.Join(", ", failures)}" });
        return list;
    }

    private static bool IsInRange(IPAddress addr, IPAddress network, int prefix)
    {
        if (addr.AddressFamily != network.AddressFamily) return false;
        var addrBytes = addr.GetAddressBytes();
        var netBytes = network.GetAddressBytes();
        var fullBytes = prefix / 8;
        var remainBits = prefix % 8;
        for (var i = 0; i < fullBytes; i++)
        {
            if (addrBytes[i] != netBytes[i]) return false;
        }
        if (remainBits == 0) return true;
        var mask = (byte)(0xFF << (8 - remainBits));
        return (addrBytes[fullBytes] & mask) == (netBytes[fullBytes] & mask);
    }
}
