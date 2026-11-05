using System.Diagnostics;
namespace WorldScience.Services;

/// <summary>
/// Finds other proxy/VPN clients that take over routes or DNS. Two tunnels with strict routes break
/// each other, and the resulting "TUN failed" error means nothing to a novice user.
/// </summary>
internal static class OtherVpnClients
{
    // Process name (lower case, without .exe) → name shown to the user. Background services that stay
    // installed while their app is closed (such as clash-verge-service) are deliberately not listed.
    private static readonly (string Process, string Name)[] Apps =
    [
        ("v2rayn", "v2rayN"), ("v2rayn.desktop", "v2rayN"), ("clash verge", "Clash Verge"), ("clash-verge", "Clash Verge"),
        ("verge-mihomo", "Clash Verge"), ("clashx", "ClashX"), ("clashx pro", "ClashX Pro"),
        ("clashx.meta", "ClashX Meta"), ("clash for windows", "Clash for Windows"), ("flclash", "FlClash"), ("flclashcore", "FlClash"),
        ("mihomo party", "Mihomo Party"), ("mihomo-party", "Mihomo Party"), ("stash", "Stash"), ("surge", "Surge"),
        ("shadowrocket", "Shadowrocket"), ("quantumult x", "Quantumult X"), ("hiddify", "Hiddify"), ("hiddifycli", "Hiddify"), ("nekoray", "NekoRay"), ("nekobox", "NekoBox"),
        ("sfm", "sing-box"), ("v2rayx", "V2RayX"), ("v2rayu", "V2rayU"), ("qv2ray", "Qv2ray"), ("shadowsocks", "Shadowsocks"),
        ("rocketbridge", "RocketBridge"), ("v2raynsimple", Earlier), ("v2raynsimple-account", Earlier)
    ];
    /// <summary>Earlier editions of this client, before it was renamed World Science (now Owlpath).</summary>
    private const string Earlier = "v2rayN Simple";
    private static readonly string[] Cores = ["xray", "sing-box", "mihomo", "clash", "clash-meta", "clash-darwin", "v2ray", "tun2socks"];

    /// <summary>Returns the friendly name of a conflicting client, or null. Processes under <paramref name="ownMarkers"/> are ours.</summary>
    internal static string? Detect(IEnumerable<(string Name, string? Path)> processes, IReadOnlyCollection<string> ownMarkers)
    {
        string? core = null;
        foreach (var (rawName, path) in processes)
        {
            if (path is not null && ownMarkers.Any(m => path.Contains(m, StringComparison.OrdinalIgnoreCase))) continue;
            var name = Path.GetFileNameWithoutExtension(rawName.Trim()).ToLowerInvariant();
            if (name.Length == 0) continue;
            // Earlier editions share one executable name; on a Mac their bundle name is what the user knows them by.
            if (name == "v2raynsimple" && LaunchAtLogin.MacBundle(path ?? "") is { } bundle) return Path.GetFileNameWithoutExtension(bundle);
            var app = Apps.FirstOrDefault(a => a.Process == name);
            if (app.Name is not null) return app.Name;
            if (core is null && Cores.Contains(name))
                core = path switch
                {
                    not null when path.Contains("v2rayN", StringComparison.OrdinalIgnoreCase) => "v2rayN",
                    not null when path.Contains("clash-verge", StringComparison.OrdinalIgnoreCase) || path.Contains("Clash Verge", StringComparison.OrdinalIgnoreCase) => "Clash Verge",
                    not null when path.Contains("ClashX", StringComparison.OrdinalIgnoreCase) => "ClashX",
                    not null when path.Contains("/PrivilegedHelperTools/com.robintheai.", StringComparison.Ordinal) => Earlier,
                    _ => name
                };
        }
        return core;
    }

    internal static IReadOnlyCollection<string> OwnMarkers(string dataDirectory) =>
        [AppContext.BaseDirectory.TrimEnd('/', '\\'), dataDirectory.TrimEnd('/', '\\'), "io.github.robintheai.worldscience.network", "World-Science", "World Science.app", "Owlpath.app"];

    /// <summary>Best effort: an unreadable process list never blocks Connect.</summary>
    internal static async Task<string?> Find(string dataDirectory, CancellationToken token = default)
    {
        try { return Detect(await Snapshot(token), OwnMarkers(dataDirectory)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    private static async Task<List<(string Name, string? Path)>> Snapshot(CancellationToken token)
    {
        if (OperatingSystem.IsWindows())
        {
            var list = new List<(string, string?)>();
            foreach (var process in Process.GetProcesses())
                using (process)
                {
                    string? path = null;
                    try { path = process.MainModule?.FileName; } catch { /* Protected or exited processes. */ }
                    list.Add((process.ProcessName, path));
                }
            return list;
        }
        // macOS: `comm` gives the full executable path for every user's processes, including root cores started by sudo.
        using var ps = Process.Start(new ProcessStartInfo("/bin/ps") { ArgumentList = { "-axo", "comm=" }, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
        if (ps is null) return [];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var output = await ps.StandardOutput.ReadToEndAsync(timeout.Token);
        await ps.WaitForExitAsync(timeout.Token);
        return ParsePs(output);
    }

    internal static List<(string Name, string? Path)> ParsePs(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => (Name: line.Contains('/') ? line[(line.LastIndexOf('/') + 1)..] : line, Path: line.Contains('/') ? line : null))
            .Select(p => ((string Name, string? Path))p).ToList();

    internal static string Message(string app) => Ui.T(
        $"{app} is running and may take over your network. Quit {app} completely, then connect again.",
        $"检测到 {app} 正在运行，它会和 Owlpath 抢占网络。请先完全退出 {app}，再点连接。");
}
