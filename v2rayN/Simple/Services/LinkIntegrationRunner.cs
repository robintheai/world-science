using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia.Media.Imaging;
namespace WorldScience.Services;

internal static class LinkIntegrationRunner
{
    internal static async Task Run(ClientController client, SimpleWindow window, string output)
    {
        Directory.CreateDirectory(output);
        var checks = new List<object>();
        void Check(bool passed, string name)
        {
            checks.Add(new { name, passed }); File.WriteAllText(Path.Combine(output, "integration.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            if (!passed) throw new InvalidOperationException(name);
        }
        static int Port() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
        var port = Port(); var socks = Port(); var second = Port(); var invalid = false; var requests = 0; var slowSubscription = false;
        string? agent = null;
        const string clashFixture = """
proxies:
  - {name: Clash SS, type: ss, server: 198.51.100.21, port: 8388, cipher: aes-256-gcm, password: "fixture:pw"}
  - {name: Clash VMess, type: vmess, server: 198.51.100.22, port: 443, uuid: 00000000-0000-4000-8000-000000000022, alterId: 0, cipher: auto, tls: true, servername: v.example.com, network: ws, ws-opts: {path: /ws, headers: {Host: v.example.com}}}
  - {name: Clash Reality, type: vless, server: 198.51.100.23, port: 443, uuid: 00000000-0000-4000-8000-000000000023, flow: xtls-rprx-vision, tls: true, servername: www.example.com, client-fingerprint: chrome, reality-opts: {public-key: QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVowMTIzNDU, short-id: 6ba85179}}
  - {name: Clash VLESS WS, type: vless, server: 198.51.100.24, port: 443, uuid: 00000000-0000-4000-8000-000000000024, tls: true, network: ws, ws-opts: {path: /vl}, skip-cert-verify: true}
  - {name: Clash Trojan, type: trojan, server: 198.51.100.25, port: 443, password: fixture, sni: t.example.com, network: grpc, grpc-opts: {grpc-service-name: tg}}
  - {name: Clash HY2, type: hysteria2, server: 198.51.100.26, port: 8443, password: fixture, sni: h.example.com}
  - {name: Clash TUIC, type: tuic, server: 198.51.100.27, port: 443, uuid: 00000000-0000-4000-8000-000000000027, password: fixture, alpn: [h3], sni: tu.example.com}
  - {name: Clash Snell, type: snell, server: 198.51.100.28, port: 443, psk: fixture}
rules:
  - MATCH,DIRECT
""";
        var fixtures = new Dictionary<string, string>
        {
            ["/clash"] = clashFixture,
            ["/expired"] = "<html>Subscription expired</html>",
            ["/sip008"] = """{"version": 1, "servers": [{"remarks": "SIP A", "server": "198.51.100.31", "server_port": 8388, "password": "fixture", "method": "aes-256-gcm"}, {"remarks": "SIP B", "server": "198.51.100.32", "server_port": 8389, "password": "fixture", "method": "chacha20-ietf-poly1305"}]}""",
            ["/safe64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("trojan://fixture@198.51.100.41:443?security=tls&sni=a.example.com#Safe%20A\ntrojan://fixture@198.51.100.42:443?security=tls&sni=b.example.com#Safe%20B\n")).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
        };
        using var stop = new CancellationTokenSource();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var server = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext c;
                try { c = await listener.GetContextAsync().WaitAsync(stop.Token); } catch { break; }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (c.Request.Url!.AbsolutePath == "/slow") await Task.Delay(10000, stop.Token);
                        else if (c.Request.Url.AbsolutePath == "/ping") c.Response.StatusCode = 204;
                        else if (c.Request.Url.AbsolutePath == "/data")
                        {
                            c.Response.ContentType = "application/octet-stream";
                            for (var n = 0; n < 100; n++) { await c.Response.OutputStream.WriteAsync(new byte[32768]); await Task.Delay(10, stop.Token); }
                        }
                        else if (fixtures.TryGetValue(c.Request.Url.AbsolutePath, out var body))
                        {
                            var bytes = Encoding.UTF8.GetBytes(body); c.Response.ContentLength64 = bytes.Length; await c.Response.OutputStream.WriteAsync(bytes);
                        }
                        else
                        {
                            Interlocked.Increment(ref requests); agent = c.Request.UserAgent;
                            if (slowSubscription) await Task.Delay(10000, stop.Token);
                            var nodes = $"socks://127.0.0.1:{socks}#Japan\nsocks://127.0.0.1:{second}#Germany\nsocks://127.0.0.1:{socks}#Japan\nsocks://127.0.0.1:{socks}#Subscription%20info";
                            var bytes = Encoding.UTF8.GetBytes(invalid ? "<html>Temporarily unavailable</html>" : Convert.ToBase64String(Encoding.UTF8.GetBytes(nodes)));
                            c.Response.ContentLength64 = bytes.Length; await c.Response.OutputStream.WriteAsync(bytes);
                        }
                    }
                    catch { }
                    finally { c.Response.Close(); }
                });
            }
        });
        Process? core = null;
        window.IsHitTestVisible = false;
        try
        {
            var version = ReleaseUpdater.CurrentVersion.ToString(3);
            Check(window.Title!.Contains(version) && window.FindControl<Button>("VersionBadge")!.Content!.ToString() == "v" + version, "current version is visible in the window title and header");
            if (OperatingSystem.IsMacOS())
                Check(NativeMenu.GetMenu(Application.Current!)!.Items.Contains(window.UpdateMenuItem!) && window.UpdateMenuItem!.Header == "Check for Updates…", "macOS application menu exposes Check for Updates");
            Check(!typeof(SimpleWindow).Assembly.GetTypes().Any(t => t.Name.Contains("Account") || t.Name.Contains("Vault")), "no account sign-in or credential vault code is compiled into World Science");
            if (window.MacMenu is { } menu)
            {
                menu.Update(client);
                menu.SavePreview(Path.Combine(output, "menu-bar.png"));
                menu.SavePreview(Path.Combine(output, "menu-bar-still.png"), CompactMenuTraffic.Title(true, 0, 0));
                menu.SavePreview(Path.Combine(output, "menu-bar-running.png"), CompactMenuTraffic.Title(true, 1_200_000, 18_400_000), 2);
                Check(menu.Width == 26 && menu.DisplayedTitle == "", "disconnected menu bar item shows only the owl, without placeholder rates");
                Check(menu.FrameCount == 12, "menu bar owl has a full 12-frame wing beat");
                Check(MacMenuBar.FrameInterval(0, false) is null && MacMenuBar.FrameInterval(500_000_000, true) is null
                    && MacMenuBar.FrameInterval(1_000, false) == TimeSpan.FromMilliseconds(220) && MacMenuBar.FrameInterval(1_000_000_000, false) == TimeSpan.FromMilliseconds(45)
                    && MacMenuBar.FrameInterval(1_000_000, false) < MacMenuBar.FrameInterval(10_000, false), "menu bar owl stays still without traffic or with reduced motion and orbits faster as traffic grows");
            }
            Check(window.FindControl<Border>("SubscriptionPanel")!.IsVisible && window.FindControl<Button>("SendCodeButton") is null, "first launch shows URL import without email or code controls");
            await Frame(window, Path.Combine(output, "subscriptions.png"));
            await window.ImportLink("not-a-link");
            Check(!client.AvailableLocations.Any() && window.FindControl<TextBlock>("SubscriptionFeedback")!.Text!.Contains("HTTP"), "invalid URL has visible feedback and no catalog mutation");
            var url = $"http://127.0.0.1:{port}/sub?token=fixture-only";
            await window.ImportLink(url);
            Check(client.AvailableLocations.Count() == 2, "paste subscription imports two real routes and removes duplicates and notice rows");
            Check(window.FindControl<Border>("HomePanel")!.IsVisible, "successful import shows Connect home immediately");
            Check((await AppManager.Instance.SubItems()).Count == 1, "only one saved subscription after import");
            Check(window.FindControl<TextBlock>("SubscriptionDetails")!.Text!.Contains("2 locations"), "home displays saved location count");
            var ids = client.AvailableLocations.Select(n => n.Id).Order().ToArray();
            await window.ImportLink(url);
            Check(requests == 1 && client.AvailableLocations.Count() == 2, "pasting the same link reuses the saved subscription without duplicates");
            Check(await client.UpdateSubscriptions(), "manual update downloads and validates saved subscriptions");
            Check(ids.SequenceEqual(client.AvailableLocations.Select(n => n.Id).Order()), "refresh preserves unchanged node identities and selection keys");
            await LinkSubscriptions.Refresh(client.Config, (await AppManager.Instance.SubItems()).Single(), Port(), CancellationToken.None);
            await client.Refresh();
            Check(ids.SequenceEqual(client.AvailableLocations.Select(n => n.Id).Order()), "broken local proxy falls back to direct subscription download without losing nodes");
            invalid = true;
            Check(!await client.UpdateSubscriptions(), "invalid subscription update reports failure");
            Check(ids.SequenceEqual(client.AvailableLocations.Select(n => n.Id).Order()), "invalid update retains all saved nodes");
            Check((await AppManager.Instance.SubItems()).Count == 1, "failed import removes staging records");
            invalid = false;
            var pending = window.ImportLink($"http://127.0.0.1:{port}/slow"); await Task.Delay(150); await window.ImportLink(""); await pending;
            Check(window.FindControl<TextBlock>("SubscriptionFeedback")!.Text!.Contains("stopped") && (await AppManager.Instance.SubItems()).Count == 1, "Cancel stops a slow download without adding an empty subscription");
            await client.Refresh();
            Check(client.AvailableLocations.Count() == 2, "catalog reload works without an account session");
            Check(ReleaseUpdater.AssetName("macos", "arm64", new Version(1,0,0)).StartsWith("World-Science-"), "update package names are World Science's own");
            var rejected = false;
            try { ReleaseUpdater.EnsureAssetUrl("https://ai.z5a.com/clients/other/releases/v1.0.34/Other-1.0.34-macos-arm64.dmg"); } catch { rejected = true; }
            Check(rejected, "packages outside World Science's mirror folder are rejected as an update source");
            if (OperatingSystem.IsMacOS())
            {
                var originalBinding = client.Config.CoreBasicItem.BindInterface;
                foreach (var coreType in new[] { ECoreType.Xray, ECoreType.sing_box })
                {
                    var node = JsonUtils.DeepCopy(client.AvailableLocations.First().Profile);
                    node.Address = "198.51.100.10"; node.CoreType = coreType;
                    var wifi = await MacTunBridge.Build(client.Config, node, "en0");
                    var ethernet = await MacTunBridge.Build(client.Config, node, "en5");
                    var main = await CoreConfigHandler.GenerateClientConfig(wifi.MainResult.Context, null);
                    var next = await CoreConfigHandler.GenerateClientConfig(ethernet.MainResult.Context, null);
                    Check(main.Success == true && next.Success == true, $"{coreType} real connection configurations generate successfully");
                    using var parsed = JsonDocument.Parse(main.Data!.ToString()!);
                    using var changed = JsonDocument.Parse(next.Data!.ToString()!);
                    var outbound = parsed.RootElement.GetProperty("outbounds").EnumerateArray().First(o => o.GetProperty("tag").GetString() == "proxy");
                    var updated = changed.RootElement.GetProperty("outbounds").EnumerateArray().First(o => o.GetProperty("tag").GetString() == "proxy");
                    string Binding(JsonElement o) => coreType == ECoreType.sing_box ? o.GetProperty("bind_interface").GetString()! : o.GetProperty("streamSettings").GetProperty("sockopt").GetProperty("interface").GetString()!;
                    Check(Binding(outbound) == "en0" && Binding(updated) == "en5", $"{coreType} actual outbound binds to the current physical interface on each connection");
                    var bridge = await CoreConfigHandler.GenerateClientConfig(wifi.PreSocksResult!.Context, null);
                    var nextBridge = await CoreConfigHandler.GenerateClientConfig(ethernet.PreSocksResult!.Context, null);
                    Check(bridge.Success == true && bridge.Data!.ToString() == nextBridge.Data!.ToString(), $"{coreType} interface changes do not reinstall the privileged bridge");
                }
                Check(client.Config.CoreBasicItem.BindInterface == originalBinding, "physical binding does not overwrite saved global settings");
            }
            // The app core routes 127.0.0.1 directly, so a check that must pass through the node uses a domain that only the
            // fixture nodes can reach: they send it to a local responder.
            const string ThroughNode = "http://probe.fallback-fixture.world-science/ping";
            var probePort = Port(); var responder = new TcpListener(IPAddress.Loopback, probePort); responder.Start();
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    using var c = await responder.AcceptTcpClientAsync(); using var s = c.GetStream(); var buffer = new byte[4096]; var request = "";
                    while (!request.Contains("\r\n\r\n")) { var n = await s.ReadAsync(buffer); if (n == 0) break; request += Encoding.ASCII.GetString(buffer, 0, n); }
                    await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                }
            });
            string FixtureCore(int inbound) => JsonSerializer.Serialize(new
            {
                inbounds = new[] { new { listen = "127.0.0.1", port = inbound, protocol = "socks", settings = new { auth = "noauth", udp = true } } },
                outbounds = new object[] { new { protocol = "freedom", tag = "direct" }, new { protocol = "freedom", tag = "probe", settings = new { redirect = $"127.0.0.1:{probePort}" } } },
                routing = new { rules = new[] { new { type = "field", domain = new[] { "full:probe.fallback-fixture.world-science" }, outboundTag = "probe" } } }
            });
            var fixture = Path.Combine(output, "fixture-core.json");
            File.WriteAllText(fixture, FixtureCore(socks));
            core = Process.Start(new ProcessStartInfo(Utils.GetBinPath(OperatingSystem.IsWindows() ? "xray.exe" : "xray", "Xray")) { ArgumentList = { "run", "-c", fixture }, UseShellExecute = false });
            await Task.Delay(500);
            client.Settings.Tun = false;
            client.Config.SpeedTestItem.SpeedPingTestUrl = $"http://127.0.0.1:{port}/ping";
            await client.ChooseLocation(client.AvailableLocations.First(n => n.Profile.Port == socks));
            // Connect must preempt an automatic refresh, not disappear behind its lock.
            slowSubscription = true;
            var before = requests;
            var refreshing = client.UpdateSubscriptions(silent: true);
            for (var n = 0; n < 100 && requests == before; n++) await Task.Delay(20);
            Check(client.Busy && requests > before, "slow automatic refresh holds the task lock before Connect");
            var connecting = client.Toggle();
            Check(client.ConnectionPending && client.ConnectionStatus == "Connecting…" && client.PowerShortLabel == "Cancel", "Connect publishes Connecting and Cancel immediately during a refresh");
            await connecting.WaitAsync(TimeSpan.FromSeconds(8)); await refreshing;
            Check(client.Connected, "Connect cancels slow automatic refresh and uses saved locations");
            slowSubscription = false; await client.Toggle();
            // Two quick clicks must cancel, never enqueue a second connection.
            var firstClick = client.Toggle(); var secondClick = client.Toggle();
            await Task.WhenAll(firstClick, secondClick).WaitAsync(TimeSpan.FromSeconds(8));
            Check(!client.Connected && !client.WantsConnection && !client.Busy, "rapid second click cancels without a queued connection or busy latch");
            window.ShowPage("home");
            window.FindControl<Button>("PowerButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            for (var n=0;n<200 && !client.Connected;n++) await Task.Delay(100);
            Check(client.Connected, "real Connect button opens the selected local proxy without authentication");
            using (var http = new HttpClient(new SocketsHttpHandler { UseProxy = true, Proxy = new WebProxy($"socks5://127.0.0.1:{AppManager.Instance.GetLocalPort(EInboundProtocol.socks)}") }) { Timeout = TimeSpan.FromSeconds(5) })
                Check((await http.GetAsync($"http://127.0.0.1:{port}/ping")).StatusCode == HttpStatusCode.NoContent, "traffic traverses the app core and selected fixture node");
            await Frame(window, Path.Combine(output, "home.png"));
            // The connect button's three scenes, rendered alone for visual QA.
            foreach (var (name, connected, busy) in new[] { ("dial-off.png", false, false), ("dial-connecting.png", false, true), ("dial-on.png", true, false) })
            {
                var dial = new WorldScience.Controls.OwlDial { Connected = connected, Pending = busy };
                dial.Measure(new Size(142, 142)); dial.Arrange(new Rect(0, 0, 142, 142));
                using var bmp = new RenderTargetBitmap(new PixelSize(284, 284), new Vector(192, 192)); bmp.Render(dial); bmp.Save(Path.Combine(output, name));
            }
            window.FindControl<Button>("PowerButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            for (var n=0;n<100 && (client.Connected || client.WantsConnection);n++) await Task.Delay(100);
            Check(!client.Connected && !client.WantsConnection, "Disconnect clears connection and recovery intent");
            window.FindControl<Button>("LocationButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(500);
            Check(window.ActiveLocationPicker?.Items.Count == 3, "Choose location shows Automatic and the two imported routes");
            var picker = window.ActiveLocationPicker!;
            client.Config.SpeedTestItem.SpeedTestUrl = $"http://127.0.0.1:{port}/data";
            picker.FindControl<Button>("TestSpeedButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(100);
            Check(client.IsTesting && client.SpeedTestLabel == "Stop test" && picker.Items.Any(i => i.Speed == "Testing…"), "Test speed click immediately starts measurement and visible row feedback");
            for (var n=0;n<100 && !client.AvailableLocations.Any(x => x.HasFreshSpeed);n++) await Task.Delay(100);
            Check(client.AvailableLocations.Any(x => x.HasFreshSpeed), "Test speed button produces a real throughput sample through the fixture proxy");
            picker.FindControl<Button>("TestSpeedButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            for (var n=0;n<100 && client.IsTesting;n++) await Task.Delay(50);
            Check(!client.IsTesting && client.SpeedTestStatus.Contains("stopped"), "Stop test cancels and displays completion feedback");
            var testing = client.MeasureLocations();
            await Task.Delay(100);
            var priorityConnect = client.Toggle();
            Check(client.ConnectionPending, "Connect takes priority while a speed test is running");
            await priorityConnect.WaitAsync(TimeSpan.FromSeconds(8)); await testing;
            Check(client.Connected && !client.IsTesting, "speed test cleanup completes before connection starts");
            await client.Toggle();
            // Fallback: a chosen location that stops working gives way to a working one and is used again once it recovers.
            var japan = client.AvailableLocations.First(n => n.Profile.Port == socks); var germany = client.AvailableLocations.First(n => n.Profile.Port == second);
            var secondFixture = Path.Combine(output, "fixture-core-2.json");
            File.WriteAllText(secondFixture, FixtureCore(second));
            Process? secondCore = null; var pingUrl = client.Config.SpeedTestItem.SpeedPingTestUrl;
            try
            {
                client.Config.SpeedTestItem.SpeedPingTestUrl = ThroughNode;
                await client.ChooseLocation(germany); client.Settings.Fallback = false;
                await client.Toggle();
                Check(!client.Connected && !client.FallbackActive, "with fallback off, a chosen location that does not work is not replaced");
                if (client.WantsConnection) await client.Toggle();
                client.Settings.Fallback = true;
                await client.Toggle();
                Check(client.Connected && client.FallbackActive && client.Config.IndexId == japan.Id && client.LocationChoice.Contains("Germany") && client.Notice.Contains("Japan"),
                    "a chosen location that does not work gives way to a working one, and the choice is kept");
                await client.VerifyRecoveryTick(DateTimeOffset.UtcNow.AddMinutes(4));
                Check(client.Connected && client.FallbackActive && client.Config.IndexId == japan.Id, "while the chosen location is still down, the working one is kept");
                secondCore = Process.Start(new ProcessStartInfo(Utils.GetBinPath(OperatingSystem.IsWindows() ? "xray.exe" : "xray", "Xray")) { ArgumentList = { "run", "-c", secondFixture }, UseShellExecute = false });
                await Task.Delay(500);
                await client.VerifyRecoveryTick(DateTimeOffset.UtcNow.AddMinutes(8));
                Check(client.Connected && !client.FallbackActive && client.Config.IndexId == germany.Id && !client.HasNotice, "once the chosen location works again, the connection switches back to it");
                secondCore.Kill(true); secondCore.WaitForExit(); secondCore.Dispose(); secondCore = null;
                var start = DateTimeOffset.UtcNow.AddMinutes(9);
                foreach (var seconds in new[] { 0, 10, 20 }) await client.VerifyRecoveryTick(start.AddSeconds(seconds));
                Check(client.Connected && client.FallbackActive && client.Config.IndexId == japan.Id, "when the chosen location dies while connected, three failed health checks move the connection to a working one");
            }
            finally
            {
                if (client.Connected || client.WantsConnection) await client.Toggle();
                client.Config.SpeedTestItem.SpeedPingTestUrl = pingUrl;
                await client.ChooseLocation(null);
                if (secondCore is { HasExited: false }) secondCore.Kill(true); secondCore?.Dispose();
            }
            Check(MacVpnConflict.PhysicalInterface("utun8 : flags 0x5 (IPv4)\n  en0 : flags 0x7 (IPv4,IPv6)") == "en0", "isolated speed core binds to physical interface rather than another VPN");
            Check(MacVpnConflict.PhysicalInterface("utun8 : flags 0x5 (IPv4)") is null, "no physical interface is guessed when none is available");
            client.Notice = "Unable to update nodes. Your saved nodes are still available.";
            // Failure surfaces once rather than simultaneously in the update and global notice regions.
            invalid = true; await client.RefreshAndRankLocations();
            Check(!client.HasNotice, "duplicate update failure notice is suppressed");
            Check(MacVpnConflict.ConnectedService("* (Connected) ID VPN (provider) \"Shadowrocket\" [VPN:provider]") == "Shadowrocket", "connected macOS VPN is identified before starting another tunnel");
            Check(MacVpnConflict.ConnectedService("* (Disconnected) ID VPN (provider) \"Shadowrocket\" [VPN:provider]") is null, "disconnected macOS VPN does not block Connect");
            await Frame(window, Path.Combine(output, "speed-feedback.png"));
            picker.Dismiss();
            invalid = false;
            Check(agent?.StartsWith("v2rayN/") == true, "subscription requests keep a v2rayN User-Agent so providers choose a compatible format");
            List<NodeRow> Of(SubItem sub) => client.AvailableLocations.Where(n => n.Profile.Subid == sub.Id).ToList();
            async Task<SubItem> Saved(Func<SubItem, bool> match) => (await AppManager.Instance.SubItems()).Single(s => s.Enabled && match(s));
            await window.ImportLink($"http://127.0.0.1:{port}/clash");
            var clashNodes = Of(await Saved(s => s.Url?.EndsWith("/clash") == true));
            Check(clashNodes.Select(n => n.Profile.Remarks).Order(StringComparer.Ordinal).SequenceEqual(["Clash HY2", "Clash Reality", "Clash SS", "Clash TUIC", "Clash Trojan", "Clash VLESS WS", "Clash VMess"]), "Clash YAML subscription imports ss, vmess, vless, trojan, hysteria2 and tuic by name and skips Snell");
            var realityNode = clashNodes.Single(n => n.Profile.Remarks == "Clash Reality").Profile;
            Check(realityNode.StreamSecurity == "reality" && realityNode.PublicKey == "QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVowMTIzNDU" && realityNode.ShortId == "6ba85179" && realityNode.GetProtocolExtra().Flow == "xtls-rprx-vision" && realityNode.Fingerprint == "chrome", "Clash reality node keeps public key, short id, flow and fingerprint");
            Check(clashNodes.Single(n => n.Profile.Remarks == "Clash VLESS WS").Profile is { AllowInsecure: "true", Network: "ws", StreamSecurity: "tls" } && clashNodes.Single(n => n.Profile.Remarks == "Clash Trojan").Profile.Network == "grpc"
                && clashNodes.Single(n => n.Profile.Remarks == "Clash VMess").Profile is { ConfigType: EConfigType.VMess, Network: "ws", StreamSecurity: "tls" }
                && clashNodes.Single(n => n.Profile.Remarks == "Clash TUIC").Profile.ConfigType == EConfigType.TUIC && clashNodes.Single(n => n.Profile.Remarks == "Clash HY2").Profile.ConfigType == EConfigType.Hysteria2, "Clash transports, TLS options and protocols survive import");
            var clashIds = clashNodes.Select(n => n.Id).Order().ToArray();
            Check(await client.UpdateSubscriptions() && clashIds.SequenceEqual(Of(await Saved(s => s.Url?.EndsWith("/clash") == true)).Select(n => n.Id).Order()), "Clash subscription refreshes with stable node identities");
            await window.ImportLink($"http://127.0.0.1:{port}/sip008");
            Check(Of(await Saved(s => s.Url?.EndsWith("/sip008") == true)).Select(n => n.Profile.Remarks).Order(StringComparer.Ordinal).SequenceEqual(["SIP A", "SIP B"]), "SIP008 JSON subscription imports Shadowsocks servers");
            await window.ImportLink($"http://127.0.0.1:{port}/safe64");
            Check(Of(await Saved(s => s.Url?.EndsWith("/safe64") == true)).Count == 2, "URL-safe unpadded base64 subscription imports");
            var subsBefore = (await AppManager.Instance.SubItems()).Count;
            await window.ImportLink("vless://broken");
            Check((await AppManager.Instance.SubItems()).Count == subsBefore && window.FindControl<TextBlock>("SubscriptionFeedback")!.Text!.Contains("No supported"), "unusable pasted links show feedback and save nothing");
            var pasted = "vless://00000000-0000-4000-8000-000000000051@198.51.100.51:443?security=tls&sni=p.example.com&type=ws&path=%2Fp#Pasted%20A trojan://fixture@198.51.100.52:443?security=tls#Pasted%20B";
            await window.ImportLink(pasted);
            var manual = await Saved(s => s.Memo?.StartsWith("manual:") == true);
            Check(manual.Url == "" && manual.Remarks == "Manual nodes" && Of(manual).Select(n => n.Profile.Remarks).Order(StringComparer.Ordinal).SequenceEqual(["Pasted A", "Pasted B"]) && window.FindControl<Border>("HomePanel")!.IsVisible, "pasted links are saved as a local Manual nodes subscription");
            await window.ImportLink(pasted);
            Check((await AppManager.Instance.SubItems()).Count == subsBefore + 1, "pasting the same nodes again does not duplicate them");
            await window.ImportLink(Convert.ToBase64String(Encoding.UTF8.GetBytes(clashFixture)));
            Check(Of(await Saved(s => s.Remarks == "Manual nodes 2")).Count == 7, "pasted base64 Clash YAML becomes a second local subscription");
            var beforeUpdate = requests;
            Check(await client.UpdateSubscriptions() && requests == beforeUpdate + 1 && client.Notice == "Nodes updated." && Of(manual).Count == 2, "Update nodes downloads only links and keeps pasted nodes without an error");
            foreach (var sub in (await AppManager.Instance.SubItems()).Where(s => !LinkSubscriptions.IsManual(s))) await ConfigHandler.DeleteSubItem(client.Config, sub.Id);
            await client.Refresh(); beforeUpdate = requests;
            Check(await client.UpdateSubscriptions() && requests == beforeUpdate && client.Notice == "Nodes updated." && client.AvailableLocations.Count() == 9, "with only pasted nodes, Update nodes succeeds without downloading anything");
            // Subscriptions saved by earlier editions are imported once, from fixture databases only.
            foreach (var sub in await AppManager.Instance.SubItems()) await ConfigHandler.DeleteSubItem(client.Config, sub.Id);
            await client.Refresh(); client.Settings.AutoConnect = false;
            var legacy = Path.Combine(Path.GetTempPath(), "ws-legacy-" + Guid.NewGuid().ToString("N"));
            void Legacy(string folder, params (string Url, bool Enabled)[] rows)
            {
                var dir = Path.Combine(legacy, folder, "guiConfigs"); Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(legacy, folder, "simple-settings.json"), "{}");
                using var db = new SQLite.SQLiteConnection(Path.Combine(dir, "guiNDB.db")); db.CreateTable<SubItem>();
                var sort = 0; foreach (var (url, enabled) in rows) db.Insert(new SubItem { Id = Guid.NewGuid().ToString("N"), Remarks = "old", Url = url, Enabled = enabled, Sort = sort++ });
            }
            var kept = $"http://127.0.0.1:{port}/sub?token=legacy"; var offline = $"http://127.0.0.1:{Port()}/offline";
            Legacy("Earlier-A", (kept, true));
            Legacy("Earlier-B", (kept, true), ($"http://127.0.0.1:{port}/expired", true), ($"http://127.0.0.1:{port}/clash", false), (offline, true), ("not a link", true));
            File.WriteAllText(Path.Combine(legacy, "Earlier-A", "simple-settings.json"), "{\"AutoConnect\": true}");
            Legacy("v2rayN", ($"http://127.0.0.1:{port}/sub?token=upstream", true)); File.Delete(Path.Combine(legacy, "v2rayN", "simple-settings.json"));
            try
            {
                var imported = await window.ImportLegacy(legacy); var subs = await AppManager.Instance.SubItems();
                Check(imported == 2 && subs.Count == 2 && subs.Any(s => s.Url == kept) && subs.Any(s => s.Url == offline && s.Enabled), "old subscriptions import once each; an offline link is kept for a later update; plain v2rayN data is left alone");
                Check(client.AvailableLocations.Count() == 2 && client.Settings.AutoConnect && client.Settings.LegacyImportDone, "imported links are downloaded into locations and Connect automatically carries over");
                Check(await window.ImportLegacy(legacy) == 0 && (await AppManager.Instance.SubItems()).Count == 2, "expired, disabled and invalid old links are left behind and import never repeats");
            }
            finally { Directory.Delete(legacy, true); }
        }
        catch (Exception ex) { Environment.ExitCode = 1; File.WriteAllText(Path.Combine(output, "integration-error.txt"), ex.ToString()); }
        finally { await client.StopForSignOut(); stop.Cancel(); listener.Stop(); if (core is { HasExited:false }) core.Kill(true); core?.Dispose(); window.IsHitTestVisible = true; }
    }
    private static async Task Frame(Window window, string path)
    {
        // 900 ms: let the connect button's scene fade finish before the shot.
        await Task.Delay(900); var previous = window.Background; window.Background = Avalonia.Media.Brushes.White; using var bmp = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width * 2, (int)window.Bounds.Height * 2), new Vector(192,192)); bmp.Render(window); bmp.Save(path); window.Background = previous;
    }
}
