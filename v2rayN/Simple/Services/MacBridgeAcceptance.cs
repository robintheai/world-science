using System.Reflection;
using ServiceLib.Models.CoreConfigs;

namespace WorldScience.Services;

/// <summary>Isolated acceptance: real outbound processes, synthetic unprivileged bridge sentinel.
/// It never asks for administrator permission or changes system routes/DNS.</summary>
internal static class MacBridgeAcceptance
{
    internal static async Task Verify(Config config, string output, Action<bool, string> check)
    {
        if (!OperatingSystem.IsMacOS()) return;
        await VerifySupervisor(output, check);
        var originalDns = config.SimpleDNSItem.RemoteDNS;
        config.SimpleDNSItem.RemoteDNS = "https://cloudflare-dns.com/dns-query,https://dns.google/dns-query";
        var manager = CoreManager.Instance;
        await manager.CoreStop();
        var gate = new MacAuthorizationPromptGate(); // no user permit: any elevation fails the test
        var oldGate = CoreAdminManager.Instance.MacPromptGate;
        CoreAdminManager.Instance.MacPromptGate = gate;
        var sentinel = new ProcessService("/bin/sleep", "90", output, false, false, null, null);
        await sentinel.StartAsync();
        var bridgePid = sentinel.Id;
        string? stableConfig = null;
        void Set(string name, object? value) => typeof(CoreManager).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(manager, value);
        try
        {
            foreach (var type in new[] { ECoreType.Xray, ECoreType.sing_box, ECoreType.Xray })
            {
                var node = new ProfileItem { CoreType = type, ConfigType = EConfigType.SOCKS, Address = "127.0.0.1", Port = 9 };
                config.TunModeItem.EnableIPv6Address = true;
                config.TunModeItem.AutoRoute = true; config.TunModeItem.StrictRoute = true;
                config.Inbound[0].SniffingEnabled = true;
                var built = await MacTunBridge.Build(config, node);
                var main = GlobalTrafficPolicy.Apply(built.MainResult.Context);
                var bridge = GlobalTrafficPolicy.Apply(built.PreSocksResult!.Context);
                check(built.Success && !main.IsTunEnabled && bridge.IsTunEnabled, type + " outbound is unprivileged and bridge owns TUN");
                var prePath = Path.Combine(output, "bridge-" + type + ".json");
                var preResult = await CoreConfigHandler.GenerateClientConfig(bridge, prePath);
                check(preResult.Success, type + " bridge config generates");
                var json = await File.ReadAllTextAsync(prePath);
                using (var parsed = System.Text.Json.JsonDocument.Parse(json))
                {
                    var rules = parsed.RootElement.GetProperty("route").GetProperty("rules").EnumerateArray().ToList();
                    check(!rules.Any(r => r.TryGetProperty("action", out var action) && action.GetString() == "sniff"),
                        "Fixed bridge avoids duplicate first-packet sniffing");
                    check(rules.Any(r => r.TryGetProperty("action", out var action) && action.GetString() == "hijack-dns"),
                        "Fixed bridge still intercepts DNS with sniffing disabled");
                }
                check(stableConfig is null || stableConfig == json, "Bridge routes and DNS stay identical across outbound protocol changes");
                stableConfig ??= json;
                var mainPath = Path.Combine(output, "main-" + type + ".json");
                await CoreConfigHandler.GenerateClientConfig(main, mainPath);
                if (type == ECoreType.sing_box)
                    check(!File.ReadAllText(mainPath).Contains("external_controller"), "Secondary sing-box does not contend for bridge statistics port");
                foreach (var (binary, args) in new[]
                {
                    (Utils.GetBinPath("sing-box", "sing_box"), new[] { "check", "-c", prePath }),
                    (Utils.GetBinPath(type == ECoreType.Xray ? "xray" : "sing-box", type.ToString()),
                        type == ECoreType.Xray ? new[] { "run", "-test", "-c", mainPath } : new[] { "check", "-c", mainPath })
                })
                {
                    var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
                    start.Environment[Global.XrayLocalAsset] = Utils.GetBinPath("");
                    foreach (var arg in args) start.ArgumentList.Add(arg);
                    using var process = Process.Start(start)!;
                    var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
                    await process.WaitForExitAsync();
                    File.WriteAllText(Path.Combine(output, "bridge-config-check.log"), await stdout + await stderr);
                    check(process.ExitCode == 0, type + " generated config accepted by bundled core (no administrator needed)");
                }
                if (!manager.HasRetainedMacTunBridge)
                {
                    Set("_processPreService", sentinel); Set("_retainableMacTunPre", true);
                    Set("_retainedMacTunConfig", stableConfig); Set("_linuxSudo", true);
                }
                await manager.LoadCore(main, bridge, retainMacTunBridge: true);
                check(manager.HasRetainedMacTunBridge && !sentinel.HasExited && sentinel.Id == bridgePid,
                    type + " node swap retains bridge PID without requesting permission");
                var changedConfig = JsonUtils.DeepCopy(bridge.AppConfig);
                changedConfig.TunModeItem.Mtu = changedConfig.TunModeItem.Mtu == 1400 ? 1500 : 1400;
                var refused = false;
                try { await manager.LoadCore(main, bridge with { AppConfig = changedConfig }, retainMacTunBridge: true); }
                catch (InvalidOperationException) { refused = true; }
                check(refused && manager.HasRetainedMacTunBridge && !sentinel.HasExited,
                    "Config drift is rejected before destroying the authorized session");
                await manager.CoreStop(keepMacTunBridge: true);
                check(!sentinel.HasExited, "Failed outbound recovery keeps the authorized bridge alive");
            }
            await manager.CoreStop();
            try { using var pid = Process.GetProcessById(bridgePid); check(pid.HasExited, "Full app shutdown ends the retained bridge"); }
            catch (ArgumentException) { check(true, "Full app shutdown ends the retained bridge"); }
        }
        finally
        {
            config.SimpleDNSItem.RemoteDNS = originalDns;
            await manager.CoreStop(); CoreAdminManager.Instance.MacPromptGate = oldGate;
            try { using var pid = Process.GetProcessById(bridgePid); if (!pid.HasExited) pid.Kill(); } catch (ArgumentException) { }
        }
    }
    private static async Task VerifySupervisor(string output, Action<bool,string> check)
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var session = Path.Combine(output, "supervisor-fixed-session"); Directory.CreateDirectory(session);
        var binary = Path.Combine(session, "core"); File.Copy(Utils.GetBinPath("sing-box", "sing_box"), binary, true);
        File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(session, "config.json"), System.Text.Json.JsonSerializer.Serialize(new {
            log = new { level = "error" }, outbounds = new[] { new { type = "direct", tag = "direct" } },
            experimental = new { clash_api = new { external_controller = $"127.0.0.1:{port}" } }
        }));
        var marker = Path.Combine(output, "supervisor.active"); File.WriteAllText(marker, "active");
        var log = Path.Combine(output, "supervisor-lifecycle.log"); File.WriteAllText(log, "");
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        foreach (var arg in new[] { "-c", MacTunSupervisor.Script(Environment.ProcessId, marker, log, "true", "true"), "fixture", session }) start.ArgumentList.Add(arg);
        using var supervisor = Process.Start(start)!;
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(1) };
        async Task<int> WaitReady(int previous)
        {
            var until = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < until)
            {
                var line = File.ReadAllLines(log).LastOrDefault(l => l.StartsWith("Owlpath tunnel child pid="));
                if (line is not null && int.TryParse(line.Split('=').Last(), out var pid) && pid != previous)
                    try { using var response = await http.GetAsync($"http://127.0.0.1:{port}/version"); if (response.IsSuccessStatusCode) return pid; } catch (HttpRequestException) { } catch (TaskCanceledException) { }
                await Task.Delay(100);
            }
            throw new InvalidOperationException("Supervised core did not become ready");
        }
        var admin = CoreAdminManager.Instance;
        var adminType = typeof(CoreAdminManager);
        var pidField = adminType.GetField("_linuxSudoPid", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var markerField = adminType.GetField("_macStopSignal", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var oldPid = pidField.GetValue(admin); var oldMarker = markerField.GetValue(admin);
        var suspendedProperty = adminType.GetProperty(nameof(CoreAdminManager.MacTunSuspended))!;
        var oldSuspended = suspendedProperty.GetValue(admin);
        pidField.SetValue(admin, supervisor.Id); markerField.SetValue(admin, marker);
        try
        {
            var first = await WaitReady(0);
            check(first > 0, "Fixed copied sing-box starts under the session supervisor (no TUN or elevation)");
            using (var child = Process.GetProcessById(first)) { child.Kill(); await child.WaitForExitAsync(); }
            var second = await WaitReady(first);
            check(second != first && !supervisor.HasExited, "Actual sing-box process crash restarts in the same session without a new permission request");
            await admin.SetMacTunSuspended(true);
            var pausedUntil = DateTime.UtcNow.AddSeconds(6);
            while (!Directory.Exists(marker + ".idle") && DateTime.UtcNow < pausedUntil) await Task.Delay(50);
            check(Directory.Exists(marker + ".idle") && !supervisor.HasExited, "Disconnect retains the authorized supervisor in idle state");
            try { using var oldChild = Process.GetProcessById(second); check(oldChild.HasExited, "Disconnect terminates the actual sing-box child"); }
            catch (ArgumentException) { check(true, "Disconnect terminates the actual sing-box child"); }
            var pausedLog = File.ReadAllText(log);
            await Task.Delay(1200);
            check(File.ReadAllText(log) == pausedLog, "Idle session does not restart the tunnel");
            var previous = second;
            for (var cycle = 0; cycle < 3; cycle++)
            {
                await admin.SetMacTunSuspended(false);
                var next = await WaitReady(previous);
                check(next != previous && !supervisor.HasExited,
                    $"Reconnect cycle {cycle + 1} reuses the same authorized supervisor");
                await admin.SetMacTunSuspended(true);
                check(Directory.Exists(marker + ".idle") && !supervisor.HasExited,
                    $"Disconnect cycle {cycle + 1} stops the tunnel while retaining the session");
                previous = next;
            }
            File.Delete(marker); await supervisor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            check(!Directory.Exists(session), "Full session shutdown removes the copied binary and config");
        }
        finally
        {
            File.Delete(marker);
            if (!supervisor.HasExited) await supervisor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            pidField.SetValue(admin, oldPid); markerField.SetValue(admin, oldMarker);
            suspendedProperty.SetValue(admin, oldSuspended);
        }
    }

}
