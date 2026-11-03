using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
namespace WorldScience.Services;

internal enum HealthAction { Healthy, Wait, Offline, Reconnect }

/// <summary>
/// Decides when a live tunnel really needs rebuilding. Rebuilding drops every open connection, so a
/// local Wi-Fi outage or a slow DNS answer must not churn nodes; only a proxy path that keeps failing does.
/// </summary>
internal sealed class HealthPolicy
{
    internal const int TunnelFailureLimit = 3;
    internal const int DnsFailureLimit = 6;
    internal static readonly TimeSpan HealthyInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan DegradedInterval = TimeSpan.FromSeconds(10);
    // A timer gap longer than this means the computer slept; probe at once instead of waiting.
    internal static readonly TimeSpan WakeGap = TimeSpan.FromSeconds(45);
    public int TunnelFailures { get; private set; }
    public int DnsFailures { get; private set; }
    public bool Degraded => TunnelFailures > 0 || DnsFailures > 0;
    public TimeSpan Interval => Degraded ? DegradedInterval : HealthyInterval;
    public void Reset() => TunnelFailures = DnsFailures = 0;

    public HealthAction Record(bool proxyOk, bool systemOk, bool networkUp)
    {
        if (proxyOk && systemOk) { Reset(); return HealthAction.Healthy; }
        // Without a physical network every node fails; waiting keeps the node and avoids a reconnect storm.
        if (!networkUp) { Reset(); return HealthAction.Offline; }
        if (!proxyOk)
        {
            DnsFailures = 0;
            if (++TunnelFailures < TunnelFailureLimit) return HealthAction.Wait;
        }
        else if (++DnsFailures < DnsFailureLimit) return HealthAction.Wait;
        Reset();
        return HealthAction.Reconnect;
    }
}

internal static class NetworkState
{
    /// <summary>True when a physical adapter is up with a routable IPv4 address (Wi-Fi or Ethernet connected).</summary>
    internal static bool PhysicalNetworkUp()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(nic =>
                nic.OperationalStatus == OperationalStatus.Up &&
                IsUplink(nic) &&
                nic.GetIPProperties().UnicastAddresses.Any(a => IsRoutable(a.Address)));
        }
        catch { return true; } // Unknown is treated as online so the normal failure path still runs.
    }
    // PPPoE broadband dial-up (very common in China) and cellular modems are not Ethernet/Wi-Fi typed.
    private static bool IsUplink(NetworkInterface nic) =>
        SystemTrafficMonitor.IsPhysical(nic.Name, nic.Description, nic.NetworkInterfaceType) ||
        nic.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2;
    internal static bool IsRoutable(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) &&
        !(address.GetAddressBytes() is [169, 254, ..]) && !(address.GetAddressBytes() is [198, 18 or 19, ..]);
}
