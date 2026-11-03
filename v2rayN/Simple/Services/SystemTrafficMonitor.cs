using System.Diagnostics;
using System.Net.NetworkInformation;
namespace WorldScience.Services;

internal readonly record struct NetworkByteCounters(long Up, long Down);

/// <summary>Physical-interface deltas, with independent baselines across adapter switches and resets.</summary>
internal sealed class NetworkRateSampler
{
    private Dictionary<string, NetworkByteCounters> _previous = [];
    private double? _time;
    internal NetworkByteCounters? Read(Dictionary<string, NetworkByteCounters> current, double seconds)
    {
        var elapsed = seconds - (_time ?? seconds);
        long up = 0, down = 0; int matched = 0;
        if (_time is not null && elapsed > 0 && elapsed <= 5)
            foreach (var (id, value) in current)
                if (_previous.TryGetValue(id, out var old) && value.Up >= old.Up && value.Down >= old.Down)
                { up += value.Up - old.Up; down += value.Down - old.Down; matched++; }
        _time = seconds; _previous = current;
        return matched == 0 ? null : new((long)Math.Round(up / elapsed), (long)Math.Round(down / elapsed));
    }
}

/// <summary>Whole-computer physical network traffic. No packet contents, addresses or process tracking.</summary>
internal sealed class SystemTrafficMonitor : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _running;
    internal void Start(Action<long,long> sample, Action missing) => _running ??= Task.Run(async () =>
    {
        var rates = new NetworkRateSampler();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                NetworkByteCounters? rate = null;
                try { rate = rates.Read(Capture(), Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency); }
                catch (Exception ex) { Trace.TraceError("System traffic unavailable: " + ex.GetType().Name); }
                if (rate is { } value) sample(value.Up, value.Down); else missing();
            } while (await timer.WaitForNextTickAsync(_stop.Token));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    });
    public void Dispose() => _stop.Cancel();

    internal static bool IsPhysical(string name, string description, NetworkInterfaceType type)
    {
        if (type is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)) return false;
        var lower = name.ToLowerInvariant();
        if (new[] { "lo", "utun", "tun", "tap", "ipsec", "awdl", "llw", "bridge", "veth", "docker", "br-", "vmnet", "vmenet", "anpi", "ap", "nan" }.Any(lower.StartsWith)) return false;
        return !new[] { "virtual", "vpn", "tunnel", "hyper-v", "wireguard", "tailscale", "zerotier", "vmware", "loopback", "tap-", "wintun" }
            .Any(word => (name + " " + description).Contains(word, StringComparison.OrdinalIgnoreCase));
    }
    internal static Dictionary<string, NetworkByteCounters> Capture()
    {
        if (OperatingSystem.IsMacOS()) return MacCounters();
        var result = new Dictionary<string, NetworkByteCounters>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || !IsPhysical(adapter.Name, adapter.Description, adapter.NetworkInterfaceType)) continue;
            var counters = adapter.GetIPStatistics();
            result[adapter.Id] = new(counters.BytesSent, counters.BytesReceived);
        }
        return result;
    }

    private static Dictionary<string, NetworkByteCounters> MacCounters()
    {
        // Apple's system utility supplies the full interface counters on current macOS.
        // The managed/native unprivileged interface snapshots can truncate or quantize
        // these values. Parse one link-layer row per physical adapter, never IP rows.
        var names = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && IsPhysical(n.Name,n.Description,n.NetworkInterfaceType))
            .Select(n => n.Name).ToHashSet(StringComparer.Ordinal);
        using var process = Process.Start(new ProcessStartInfo("/usr/sbin/netstat")
        {
            ArgumentList = { "-ibn" }, UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        }) ?? throw new IOException("Cannot read physical network counters");
        var reading = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(2000)) { process.Kill(true); throw new IOException("Physical network counter timeout"); }
        if (process.ExitCode != 0) throw new IOException("Physical network counters unavailable");
        return ParseMacCounters(reading.GetAwaiter().GetResult(),names);
    }
    internal static Dictionary<string, NetworkByteCounters> ParseMacCounters(string text, HashSet<string> physicalNames)
    {
        var result = new Dictionary<string, NetworkByteCounters>();
        var rows = text.Split('\n',StringSplitOptions.RemoveEmptyEntries);
        if (rows.Length == 0) return result;
        var header = rows[0].Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
        var input = Array.IndexOf(header,"Ibytes"); var output = Array.IndexOf(header,"Obytes");
        if (input < 0 || output < 0) throw new InvalidDataException("Unknown physical counter format");
        foreach (var row in rows.Skip(1))
        {
            var parts = row.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= Math.Max(input,output) || !physicalNames.Contains(parts[0]) || !parts[2].StartsWith("<Link#",StringComparison.Ordinal)) continue;
            if (long.TryParse(parts[input],out var down) && long.TryParse(parts[output],out var up) && up >= 0 && down >= 0)
                result[parts[0]] = new(up,down);
        }
        return result;
    }
}
