using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace ServiceLib.Manager;

/// <summary>Temporary macOS resolver owned by the TUN lifetime; never edits saved network preferences.</summary>
public static class MacTunDns
{
    public static string? ResolverAddress(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("inbounds", out var inbounds)) return null;
        foreach (var inbound in inbounds.EnumerateArray())
        {
            if (!inbound.TryGetProperty("type", out var type) || type.GetString() != "tun") continue;
            if (!inbound.TryGetProperty("address", out var addresses)) continue;
            foreach (var address in addresses.EnumerateArray())
            {
                var parts = (address.GetString() ?? "").Split('/');
                if (parts.Length != 2 || parts[1] != "30" || !IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) continue;
                var bytes = ip.GetAddressBytes();
                // sing-box's default DNS destination is the next address in its TUN /30.
                if ((bytes[3] & 3) != 1) continue;
                bytes[3]++;
                return new IPAddress(bytes).ToString();
            }
            throw new InvalidOperationException("无法确定 TUN 的本地 DNS 地址");
        }
        return null;
    }

    public static string Key(string id) => Guid.TryParseExact(id, "N", out _) ?
        "State:/Network/Service/io.github.robintheai.worldscience." + id + "/DNS" : throw new ArgumentException("Invalid resolver owner");
    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
    public static string Install(string id, string address)
    {
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("Invalid DNS address");
        var commands = $"d.init\nd.add ServerAddresses * {ip}\nd.add SupplementalMatchDomains * \"\"\nd.add SupplementalMatchOrders # 1\nd.add SearchOrder # 1\nset {Key(id)}\nquit\n";
        return "/usr/bin/printf %s " + Quote(commands) + " | /usr/sbin/scutil; " +
            "/usr/bin/printf %s " + Quote($"show {Key(id)}\nquit\n") + " | /usr/sbin/scutil | /usr/bin/grep -Fq " + Quote(ip.ToString());
    }
    public static string Remove(string id) => "/usr/bin/printf %s " + Quote($"remove {Key(id)}\nquit\n") + " | /usr/sbin/scutil > /dev/null";
    public const string Flush = "/usr/bin/dscacheutil -flushcache; /usr/bin/killall -HUP mDNSResponder 2>/dev/null || true";
}
