using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using WorldScience.Services;

// Read-only support diagnostic: never starts a core, requests permission, or reads credentials.
if (args.Length == 4 && args[0] == "--inspect-installed-helper")
{
    var directory = args[3];
    var config = ServiceLib.Manager.MacPersistentTun.PrepareConfiguration(File.ReadAllText(args[1]), directory).Config;
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    var version = File.ReadAllText("/Applications/Owlpath.app/Contents/MacOS/Assets/network-helper.version").Trim();
    var expected = Hash(Encoding.UTF8.GetBytes("network-helper-1\n" + version + "\n" + Hash(File.ReadAllBytes(args[2])) + "\n" + Hash(config)));
    Console.WriteLine(JsonSerializer.Serialize(new { matchesInstalled = expected == File.ReadAllText(Path.Combine(directory, "fingerprint")).Trim(), expected, configHash = Hash(config) }));
    return;
}

int passed = 0;
void Assert(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); passed++; }
foreach (var label in new[] { "⏰ qa@example.invalid服务于2027-10-06到期", "📊 流量：已用 1GB / 共 1000GB", "🛒 解锁不了更换节点", "公告：请更新订阅", "Subscription info", "Monthly quota: 1000 GB" })
    Assert(SubscriptionLocations.IsNotice(label), "subscription annotation cannot become a location: " + label);
foreach (var label in new[] { "🇺🇸 美国 1", "Japan · VLESS Reality", "Germany Netflix unlock", "United States 9 · TUIC" })
    Assert(!SubscriptionLocations.IsNotice(label), "real location is retained: " + label);
Assert(CompactMenuTraffic.Title(true, 35000, 249000) == "35K/s↑\n249K/s↓", "menu bar uses two compact traffic rows without country or status text");
Assert(CompactMenuTraffic.Title(false, 35000, 249000) == "—↑\n—↓", "disconnected menu bar does not imply background device traffic is VPN traffic");
Assert(CompactMenuTraffic.Rate(long.MaxValue).Length <= 7 && CompactMenuTraffic.Rate(-1) == "0B/s", "menu traffic has bounded labels for extreme and reset counters");
Assert(CountryLocation.Name("🇺🇸 美国 2 · VLESS Reality") == "United States", "country ignores node number and protocol");
Assert(CountryLocation.Name("United States US-VPS AnyTLS") == CountryLocation.Name("美国 1"), "same country groups heterogeneous nodes");
Assert(CountryLocation.Name("🇳🇿 新西兰 01") == "New Zealand", "New Zealand country recognized");
Assert(CountryLocation.Name("unlabelled") == "Other locations", "unknown names do not leak node details");
var payload = Encoding.UTF8.GetBytes("controlled-update-fixture");
var hash = Convert.ToHexString(SHA256.HashData(payload));
var asset = ReleaseUpdater.AssetName("macos", "arm64", new Version(1, 0, 1));
string Release(string name) => JsonSerializer.Serialize(new { tag_name = "v1.0.1", prerelease = false, draft = false, body = "Release fixture", html_url = "https://ai.z5a.com/clients/world-science/", assets = new[] { new { name, browser_download_url = $"https://ai.z5a.com/clients/world-science/releases/v1.0.1/{name}" }, new { name = "SHA256SUMS", browser_download_url = "https://ai.z5a.com/clients/world-science/releases/v1.0.1/SHA256SUMS" } } });
string GitHubRelease(string name) => JsonSerializer.Serialize(new { tag_name = "v1.0.2", prerelease = false, draft = false, body = "GitHub fixture", html_url = "https://github.com/robintheai/world-science/releases/tag/v1.0.2", assets = new[] { new { name, browser_download_url = $"https://github.com/robintheai/world-science/releases/download/v1.0.2/{name}", digest = (string?)null }, new { name = "SHA256SUMS", browser_download_url = "https://github.com/robintheai/world-science/releases/download/v1.0.2/SHA256SUMS", digest = (string?)"sha256:00" } } });
var fail = false; var tamper = false;
using var http = new HttpClient(new Fixture(req =>
{
    var path = req.RequestUri!.AbsolutePath;
    if (path.EndsWith("/latest.json")) return new(HttpStatusCode.OK) { Content = new StringContent(Release(asset)) };
    if (fail) { fail = false; return new(HttpStatusCode.ServiceUnavailable); }
    if (path.EndsWith("SHA256SUMS")) return new(HttpStatusCode.OK) { Content = new StringContent($"{hash}  {asset}\n") };
    return new(HttpStatusCode.OK) { Content = new ByteArrayContent(tamper ? [0, 1] : payload) };
}));
var updater = new ReleaseUpdater(http);
var release = await updater.Check(new Version(1, 0, 0), "macos", "arm64");
Assert(release?.Version == new Version(1, 0, 1), "older client discovers newer stable release");
Assert(await updater.Check(new Version(1, 0, 1), "macos", "arm64") is null, "current version does not prompt");
Assert(ReleaseUpdater.AssetName("windows", "arm64", new Version(1, 0, 1)).EndsWith("windows-arm64.zip"), "architecture-specific Windows package");
try { await updater.Check(new Version(1, 0, 0), "macos", "x64"); throw new Exception("expected failure"); } catch (InvalidOperationException) { Assert(true, "missing architecture reports failure"); }
try { ReleaseUpdater.EnsureAssetUrl("https://example.com/update.zip"); throw new Exception("expected failure"); } catch (InvalidOperationException) { Assert(true, "foreign update source rejected"); }
using (var mirrorDown = new HttpClient(new Fixture(req => req.RequestUri!.Host == "api.github.com" ? new(HttpStatusCode.OK) { Content = new StringContent(GitHubRelease(ReleaseUpdater.AssetName("macos", "arm64", new Version(1, 0, 2)))) } : new(HttpStatusCode.BadGateway))))
{
    var fallback = await new ReleaseUpdater(mirrorDown).Check(new Version(1, 0, 0), "macos", "arm64");
    Assert(fallback?.Version == new Version(1, 0, 2) && fallback.Package.Url.StartsWith("https://github.com/robintheai/world-science/releases/download/"), "an unreachable website mirror falls back to GitHub releases");
}
ReleaseUpdater.EnsureAssetUrl("https://github.com/robintheai/world-science/releases/download/v2.0.0/World-Science-2.0.0-windows-x64.zip");
foreach (var bad in new[] { "https://github.com/someone/world-science/releases/download/v2.0.0/World-Science-2.0.0-windows-x64.zip", "https://ai.z5a.com/clients/other/releases/v1.1.1/Other-1.1.1-macos-arm64.dmg", "http://ai.z5a.com/clients/world-science/releases/v1.0.0/SHA256SUMS" })
{
    try { ReleaseUpdater.EnsureAssetUrl(bad); throw new Exception("expected failure: " + bad); } catch (InvalidOperationException) { }
}
Assert(true, "only this repository's GitHub releases and the HTTPS website mirror are accepted as package sources");
using (var missingFeed = new HttpClient(new Fixture(_ => new(HttpStatusCode.NotFound))))
{
    try { await new ReleaseUpdater(missingFeed).Check(new Version(1,0,0), "macos", "arm64"); throw new Exception("expected failure"); }
    catch (HttpRequestException) { Assert(true, "missing update feed reports failure rather than claiming up to date"); }
}
var temp = Path.Combine(Path.GetTempPath(), "simple-smoke-" + Guid.NewGuid()); Directory.CreateDirectory(temp);
try
{
    var progress = new Progress<double>();
    var path = await updater.Download(release!, temp, progress, default);
    Assert(File.ReadAllBytes(path).SequenceEqual(payload), "download checksum passes");
    tamper = true;
    try { await updater.Download(release!, temp, progress, default); throw new Exception("expected failure"); } catch (InvalidOperationException) { Assert(!File.Exists(path + ".part"), "corrupt package rejected and partial file removed"); }
    tamper = false; fail = true;
    await updater.Download(release!, temp, progress, default); Assert(true, "temporary network failure automatically retries");
    using var unavailable = new HttpClient(new Fixture(_ => new(HttpStatusCode.ServiceUnavailable)));
    try { await new ReleaseUpdater(unavailable).Download(release!, temp, progress, default); throw new Exception("expected failure"); } catch (HttpRequestException) { Assert(true, "persistent network failure surfaces after retries"); }
    var requests = 0;
    using var interrupted = new HttpClient(new Fixture(req =>
    {
        if (req.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS")) return new(HttpStatusCode.OK) { Content = new StringContent($"{hash}  {asset}\n") };
        requests++;
        if (requests == 1) return new(HttpStatusCode.OK) { Content = new StreamContent(new InterruptedStream(payload)) };
        Assert(req.Headers.Range?.Ranges.Single().From == 8, "interrupted download resumes at received byte offset");
        var content = new ByteArrayContent(payload[8..]); content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(8, payload.Length - 1, payload.Length);
        return new(HttpStatusCode.PartialContent) { Content = content };
    }));
    var resumed = await new ReleaseUpdater(interrupted).Download(release!, temp, progress, default);
    Assert(requests == 2 && File.ReadAllBytes(resumed).SequenceEqual(payload), "resumed package passes complete SHA256 verification");
    Directory.CreateDirectory(Path.Combine(temp, "guiConfigs"));
    File.WriteAllText(Path.Combine(temp, "guiConfigs", "fixture.json"), "{\"fixture\":true}");
    File.WriteAllText(Path.Combine(temp, "simple-settings.json"), "{\"Tun\":true}");
    using (var backup = ZipFile.OpenRead(ReleaseUpdater.BackupConfiguration(temp))) Assert(backup.Entries.Count == 2, "pre-upgrade backup contains settings and config");
    var measurementTime = new FixtureTime();
    using var streamHttp = new HttpClient(new Fixture(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new PacedStream(new byte[1_000_000], measurementTime)) }));
    long observed = 0; var result = await StreamingBenchmark.Measure(streamHttp, "https://fixture.test/data", TimeSpan.FromSeconds(10), 400_000, n => observed += n, default, measurementTime);
    Assert(result.Bytes == 400_000 - 32_768 && observed == 400_000 && result.MegabytesPerSecond > 0, "benchmark excludes first buffer from timed bytes and respects total data cap");
    using var cts = new CancellationTokenSource(); cts.Cancel();
    try { await StreamingBenchmark.Measure(streamHttp, "https://fixture.test/data", TimeSpan.FromSeconds(1), 10, _ => { }, cts.Token); throw new Exception("expected failure"); } catch (OperationCanceledException) { Assert(true, "cancelled speed test stops"); }
    using var badHttp = new HttpClient(new Fixture(_ => new(HttpStatusCode.Forbidden)));
    try { await StreamingBenchmark.Measure(badHttp, "https://fixture.test/data", TimeSpan.FromSeconds(1), 10, _ => { }, default); throw new Exception("expected failure"); } catch (HttpRequestException) { Assert(true, "failed HTTP response is not reported as speed"); }
    using var slowHeaders = new HttpClient(new Fixture(_ =>
    { measurementTime.Advance(600); return new(HttpStatusCode.OK) { Content = new StreamContent(new PacedStream(new byte[1_000_000], measurementTime)) }; }));
    var transferStarted = measurementTime.GetTimestamp();
    var transfer = await StreamingBenchmark.Measure(slowHeaders, "https://fixture.test/data", TimeSpan.FromSeconds(2), 400_000, _ => { }, default, measurementTime);
    Assert(measurementTime.GetElapsedTime(transferStarted) - transfer.Duration == TimeSpan.FromMilliseconds(615), "download rate excludes handshake, headers and first buffer");
    Assert(transfer.MegabytesPerSecond == result.MegabytesPerSecond, "equal data streams retain equal rates despite different setup times");
    using var slowStream = new HttpClient(new Fixture(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new SlowPacedStream(measurementTime)) }));
    var slowResult = await StreamingBenchmark.Measure(slowStream, "https://fixture.test/slow", TimeSpan.FromSeconds(6), 1_000_000, _ => { }, default, measurementTime);
    Assert(slowResult.Bytes > 1024 && slowResult.Bytes < 65536 && slowResult.MegabytesPerSecond > 0, "valid slow transfers below 64 KiB are measured instead of marked unavailable");
    var endpoints = new List<string>();
    using var fallback = new HttpClient(new Fixture(r => {
        endpoints.Add(r.RequestUri!.AbsolutePath);
        return r.RequestUri.AbsolutePath == "/blocked" ? new(HttpStatusCode.Forbidden) : new(HttpStatusCode.OK) { Content = new StreamContent(new PacedStream(new byte[1_000_000], measurementTime)) };
    }));
    var fallbackResult = await StreamingBenchmark.MeasureWithFallback(fallback, ["https://fixture.test/blocked", "https://fixture.test/data"], TimeSpan.FromSeconds(2), 400_000, _ => { }, default, measurementTime);
    Assert(endpoints.SequenceEqual(new[] { "/blocked", "/data" }) && fallbackResult.MegabytesPerSecond > 0, "blocked benchmark endpoint falls back to real data using the same client and route");
    endpoints.Clear();
    try { await StreamingBenchmark.MeasureWithFallback(fallback, ["https://fixture.test/blocked", "https://fixture.test/data"], TimeSpan.FromSeconds(2), 400_000, _ => { }, cts.Token, measurementTime); throw new Exception("cancel ignored"); }
    catch (OperationCanceledException) { Assert(endpoints.Count == 0, "cancelled speed test does not start fallback downloads"); }
    using var html = new HttpClient(new Fixture(_ => new(HttpStatusCode.OK) { Content = new StringContent("<html>portal</html>", Encoding.UTF8, "text/html") }));
    try { await StreamingBenchmark.Measure(html, "https://fixture.test/", TimeSpan.FromSeconds(2), 400_000, _ => { }, default); throw new Exception("page measured"); }
    catch (HttpRequestException) { Assert(true, "HTTP 200 captive portal is not a download speed"); }
    using var tiny = new HttpClient(new Fixture(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[100]) }));
    try { await StreamingBenchmark.Measure(tiny, "https://fixture.test/", TimeSpan.FromSeconds(2), 400_000, _ => { }, default); throw new Exception("tiny response measured"); }
    catch (HttpRequestException) { Assert(true, "tiny sample cannot create a misleading speed"); }
    using var compressed = new HttpClient(new Fixture(_ =>
    { var content = new ByteArrayContent(new byte[500_000]); content.Headers.ContentEncoding.Add("gzip"); return new(HttpStatusCode.OK) { Content = content }; }));
    try { await StreamingBenchmark.Measure(compressed, "https://fixture.test/", TimeSpan.FromSeconds(2), 400_000, _ => { }, default); throw new Exception("compressed response measured"); }
    catch (HttpRequestException) { Assert(true, "compressed response cannot inflate download throughput"); }
    var sampleIndex = 0;
    using var latency = new HttpClient(new Fixture(_ =>
    { measurementTime.Advance(new[] { 600, 35, 250, 45 }[sampleIndex++]); return new(HttpStatusCode.NoContent); }));
    var median = await LatencyBenchmark.Measure(latency, "https://fixture.test/ping", TimeSpan.FromSeconds(2), default, measurementTime);
    Assert(sampleIndex == 4 && median == 45, "latency discards cold request and uses median to resist one slow sample");
    using var slowLatency = new HttpClient(new AsyncFixture(async (_, token) =>
    { await Task.Delay(210, token); return new(HttpStatusCode.NoContent); }));
    Assert(await LatencyBenchmark.Measure(slowLatency, "https://fixture.test/", TimeSpan.FromSeconds(1), default) >= 190, "genuinely high latency is preserved rather than clamped");
    try { await LatencyBenchmark.Measure(html, "https://fixture.test/", TimeSpan.FromSeconds(1), default); throw new Exception("portal accepted"); }
    catch (HttpRequestException) { Assert(true, "latency rejects a non-204 response"); }
    using var cancelledLatency = new CancellationTokenSource(40);
    try { await LatencyBenchmark.Measure(slowLatency, "https://fixture.test/", TimeSpan.FromSeconds(1), cancelledLatency.Token); throw new Exception("cancellation ignored"); }
    catch (OperationCanceledException) { Assert(true, "latency sampling stops on cancellation"); }

}
finally { Directory.Delete(temp, true); }
// Behavioral tests: a hung primary must not delay a healthy secondary, and
// neither captive portals nor external cancellation can be reported as success.
var losingRequestsCancelled = 0;
using (var racing = new HttpClient(new AsyncFixture(async (request, token) =>
{
    if (request.RequestUri!.Host == "slow.invalid")
    {
        try { await Task.Delay(3000, token); }
        catch (OperationCanceledException) { Interlocked.Increment(ref losingRequestsCancelled); throw; }
    }
    else await Task.Delay(30, token);
    return new(HttpStatusCode.NoContent);
})))
{
    var clock = Stopwatch.StartNew();
    await ConnectivityProbe.Check(racing, ["https://slow.invalid/", "https://fast.invalid/"], TimeSpan.FromSeconds(2), default);
    Assert(clock.ElapsedMilliseconds < 1000 && losingRequestsCancelled == 1, "healthy secondary wins without waiting for stalled primary; losing request is cancelled");
}
using (var portal = new HttpClient(new Fixture(_ => new(HttpStatusCode.OK))))
{
    try { await ConnectivityProbe.Check(portal, ["https://portal.invalid/"], TimeSpan.FromSeconds(1), default); throw new Exception("expected failure"); }
    catch (HttpRequestException) { Assert(true, "HTTP 200 captive portal is not connectivity success"); }
}
using (var stalled = new HttpClient(new AsyncFixture(async (_, token) => { await Task.Delay(3000, token); return new(HttpStatusCode.NoContent); })))
{
    var clock = Stopwatch.StartNew();
    try { await ConnectivityProbe.Check(stalled, ["https://a.invalid/", "https://b.invalid/"], TimeSpan.FromMilliseconds(150), default); throw new Exception("expected timeout"); }
    catch (HttpRequestException) { Assert(clock.ElapsedMilliseconds < 1000, "unavailable endpoints share one timeout budget"); }
    using var cancel = new CancellationTokenSource(50);
    try { await ConnectivityProbe.Check(stalled, ["https://a.invalid/"], TimeSpan.FromSeconds(2), cancel.Token); throw new Exception("expected cancellation"); }
    catch (OperationCanceledException) { Assert(true, "user cancellation remains cancellation, not node failure"); }
}
var meter = new NetworkRateSampler();
Dictionary<string,NetworkByteCounters> Counts(long up,long down,string id="en0") => new() { [id] = new(up,down) };
Assert(meter.Read(Counts(50_000_000_000,70_000_000_000),0) is null, "physical meter starts with a baseline");
Assert(meter.Read(Counts(50_000_002_500,70_000_005_000),2.5) == new NetworkByteCounters(1000,2000), "physical meter uses elapsed time and preserves 64-bit counters");
Assert(meter.Read(Counts(9_000_000_000,10_000_000_000,"en5"),3.5) is null, "adapter change cannot create a spike");
Assert(meter.Read(Counts(9_000_001_000,10_000_002_000,"en5"),4.5) == new NetworkByteCounters(1000,2000), "new adapter establishes its own baseline");
Assert(meter.Read(Counts(10,20,"en5"),5.5) is null, "counter reset cannot create a negative rate");
Assert(meter.Read(Counts(5000,6000,"en5"),20) is null, "wake from sleep discards stale interval");
Assert(meter.Read(Counts(5000,6000,"en5"),21) == new NetworkByteCounters(0,0), "idle physical network reports actual zero");
Assert(meter.Read(new(),22) is null, "missing network is unavailable rather than a fake zero");
Assert(!SystemTrafficMonitor.IsPhysical("utun67","Tunnel",System.Net.NetworkInformation.NetworkInterfaceType.Unknown)
 && !SystemTrafficMonitor.IsPhysical("awdl0","awdl0",System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211)
 && !SystemTrafficMonitor.IsPhysical("vEthernet","Hyper-V Virtual Ethernet",System.Net.NetworkInformation.NetworkInterfaceType.Ethernet), "virtual interfaces are excluded to avoid duplicate bytes");
Assert(SystemTrafficMonitor.IsPhysical("en0","en0",System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211)
 && SystemTrafficMonitor.IsPhysical("Ethernet","Realtek PCIe",System.Net.NetworkInformation.NetworkInterfaceType.Ethernet), "physical Wi-Fi and Ethernet are included");
var nativeFixture = "Name Mtu Network Address Ipkts Ierrs Ibytes Opkts Oerrs Obytes Coll\nen0 1500 <Link#14> unused 1 0 70000000000 1 0 50000000000 0\nen0 1500 IPv4 unused 1 0 70000000000 1 0 50000000000 0\nutun0 1500 <Link#19> unused 1 0 70000000000 1 0 50000000000 0";
var parsedCounters = SystemTrafficMonitor.ParseMacCounters(nativeFixture,new HashSet<string> { "en0" });
Assert(parsedCounters.Count == 1 && parsedCounters["en0"] == new NetworkByteCounters(50000000000,70000000000), "macOS full counters count one link row and exclude tunnel/IP duplicates");
Assert(ServiceLib.Manager.MacTunDns.ResolverAddress("""{"inbounds":[{"type":"tun","address":["172.18.0.1/30"]}]}""") == "172.18.0.2", "TUN resolver uses the virtual peer, not the LAN gateway");
Assert(ServiceLib.Manager.MacTunDns.ResolverAddress("""{"inbounds":[{"type":"mixed"}]}""") is null, "non-TUN launch leaves DNS untouched");
try { ServiceLib.Manager.MacTunDns.ResolverAddress("""{"inbounds":[{"type":"tun","address":["172.18.0.0/30"]}]}"""); throw new Exception("expected invalid peer"); }
catch (InvalidOperationException) { Assert(true, "invalid virtual peer is rejected"); }
try { ServiceLib.Manager.MacTunDns.Install("bad\nkey", "172.18.0.2"); throw new Exception("expected invalid owner"); }
catch (ArgumentException) { Assert(true, "resolver ownership rejects command injection"); }
var permissionGate = new ServiceLib.Manager.MacAuthorizationPromptGate();
Assert(!permissionGate.TryConsume(), "Background startup cannot open a macOS password prompt");
permissionGate.BeginUserConnect();
Assert(permissionGate.TryConsume(), "An explicit Connect permits one OS prompt");
Assert(!permissionGate.TryConsume(), "Node failover cannot consume another password prompt");
Assert(!permissionGate.TryConsume(), "Repeated retries after denial do not request authorization");
permissionGate.BeginUserConnect(); permissionGate.EndUserConnect();
Assert(!permissionGate.TryConsume(), "Unused permission expires when the user action ends");
permissionGate.BeginUserConnect();
var consumed = 0;
Parallel.For(0, 100, _ => { if (permissionGate.TryConsume()) Interlocked.Increment(ref consumed); });
Assert(consumed == 1, "Concurrent requests can consume only one explicit authorization");
permissionGate.BeginUserConnect();
Assert(permissionGate.TryConsume(), "A later deliberate Connect can retry permission");
if (OperatingSystem.IsMacOS())
{
    var root = Path.Combine(Path.GetTempPath(), "ws-supervisor-check-" + Guid.NewGuid().ToString("N"));
    var session = Path.Combine(root, "session"); Directory.CreateDirectory(session);
    var marker = Path.Combine(root, "active"); File.WriteAllText(marker, "active");
    var log = Path.Combine(root, "log"); var starts = Path.Combine(root, "starts");
    var core = Path.Combine(session, "core");
    File.WriteAllText(core, "#!/bin/sh\necho started >> '" + starts + "'\nexit 7\n");
    File.SetUnixFileMode(core, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    var script = ServiceLib.Manager.MacTunSupervisor.Script(Environment.ProcessId, marker, log, "true", "true");
    var start = new System.Diagnostics.ProcessStartInfo("/bin/sh") { UseShellExecute = false };
    start.ArgumentList.Add("-c"); start.ArgumentList.Add(script); start.ArgumentList.Add("fixture"); start.ArgumentList.Add(session);
    using var supervisor = System.Diagnostics.Process.Start(start)!;
    try
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < deadline && (!File.Exists(starts) || File.ReadAllLines(starts).Length < 3)) await Task.Delay(100);
        Assert(File.ReadAllLines(starts).Length >= 3 && !supervisor.HasExited, "Session supervisor survives repeated core exits and restarts without authorization");
        File.WriteAllText(marker, "idle");
        var idleDeadline = DateTime.UtcNow.AddSeconds(5);
        while (!Directory.Exists(marker + ".idle") && DateTime.UtcNow < idleDeadline) await Task.Delay(50);
        Assert(Directory.Exists(marker + ".idle") && !supervisor.HasExited, "disconnect pauses the authorized session without discarding it");
        var pausedStarts = File.ReadAllLines(starts).Length;
        await Task.Delay(1200);
        Assert(File.ReadAllLines(starts).Length == pausedStarts, "paused session cannot restart its tunnel");
        File.WriteAllText(marker, "active");
        var resumeDeadline = DateTime.UtcNow.AddSeconds(5);
        while (File.ReadAllLines(starts).Length == pausedStarts && DateTime.UtcNow < resumeDeadline) await Task.Delay(50);
        Assert(File.ReadAllLines(starts).Length > pausedStarts && !supervisor.HasExited, "same authorized supervisor resumes the fixed core without a new prompt");
        File.Delete(marker); await supervisor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert(!Directory.Exists(session), "Ending the application session removes the fixed session and stops recovery");
        var count = File.ReadAllLines(starts).Length; await Task.Delay(1200);
        Assert(File.ReadAllLines(starts).Length == count, "Disconnected session cannot restart in the background");
    }
    finally { if (!supervisor.HasExited) supervisor.Kill(true); Directory.Delete(root, true); }
}
var fixedRules = Path.Combine(Path.GetTempPath(), "ws-rules-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixedRules);
try
{
    var rulePath = Path.Combine(fixedRules, "private.srs"); File.WriteAllBytes(rulePath, [1, 2, 3, 4]);
    var config = JsonSerializer.Serialize(new { route = new { rule_set = new[] { new { type = "local", path = rulePath } } } });
    var prepared = ServiceLib.Manager.MacPersistentTun.PrepareConfiguration(config, "/fixed-helper");
    var text = Encoding.UTF8.GetString(prepared.Config);
    Assert(!text.Contains(rulePath) && text.Contains("/fixed-helper/rule-"), "privileged configuration references installed immutable rules only");
    Assert(prepared.Resources.Count == 1 && prepared.Resources.Values.Single() == rulePath, "all local rule dependencies are included in the install snapshot");
    var same = ServiceLib.Manager.MacPersistentTun.PrepareConfiguration(config, "/fixed-helper");
    Assert(prepared.Config.SequenceEqual(same.Config), "ordinary reconnects produce the same installed configuration fingerprint");
    File.WriteAllBytes(rulePath, [5, 6, 7, 8]);
    var changed = ServiceLib.Manager.MacPersistentTun.PrepareConfiguration(config, "/fixed-helper");
    Assert(!prepared.Config.SequenceEqual(changed.Config), "changed network rules require an explicit authorized upgrade");
    try { ServiceLib.Manager.MacPersistentTun.PrepareConfiguration("{\"route\":{\"rule_set\":[{\"type\":\"remote\",\"url\":\"https://example.invalid/rules\"}]}}", "/fixed-helper"); throw new Exception("remote rules accepted"); }
    catch (InvalidOperationException) { Assert(true, "privileged helper refuses mutable remote rules"); }
}
finally { Directory.Delete(fixedRules, true); }
var installFixture = Path.Combine(Path.GetTempPath(), "ws install's " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(installFixture);
try
{
    var names = new[] { "helper", "core", "config.json", "dns-on.sh", "dns-off.sh", "fingerprint", "service.plist" };
    foreach (var name in names) File.WriteAllText(Path.Combine(installFixture, name), "fixture");
    var command = await ServiceLib.Manager.MacPersistentTun.InstallationCommand(501, installFixture, names);
    Assert(command.Contains("/bin/chmod 600") && command.Contains("shasum -a 256"), "installation resets permissions and verifies copied resources before launch");
    try { await ServiceLib.Manager.MacPersistentTun.InstallationCommand(501, installFixture, ["../../untrusted"]); throw new Exception("path traversal accepted"); }
    catch (ArgumentException) { Assert(true, "installer rejects unrecognized resource paths"); }
    try { await ServiceLib.Manager.MacPersistentTun.InstallationCommand(0, installFixture, names); throw new Exception("root owner accepted"); }
    catch (ArgumentOutOfRangeException) { Assert(true, "installer cannot register root as its requesting user"); }
    if (!OperatingSystem.IsWindows())
    {
        var syntax = new ProcessStartInfo("/bin/sh") { ArgumentList = { "-n", "-c", command }, UseShellExecute = false };
        using var shell = Process.Start(syntax)!; await shell.WaitForExitAsync();
        Assert(shell.ExitCode == 0, "authorized installation script parses with spaces and quotes in the source path without executing it");
        var functionStart = command.IndexOf("load_service()", StringComparison.Ordinal);
        var functionEnd = command.IndexOf("}; ", functionStart, StringComparison.Ordinal) + 2;
        var retryFunction = command[functionStart..functionEnd].Replace("/bin/launchctl", "launchctl_fixture").Replace("/bin/sleep 1", ":");
        foreach (var succeeds in new[] { true, false })
        {
            // Exercise the generated retry function with inert commands, never the real launchd.
            var script = "attempts=0; plist=fixture; launchctl_fixture() { [ \"$1\" = bootstrap ] || return 1; attempts=$((attempts+1)); " +
                (succeeds ? "[ \"$attempts\" -ge 3 ]; " : "return 1; ") + "}; " + retryFunction +
                " load_service; result=$?; " + (succeeds ? "[ \"$result\" = 0 ] && [ \"$attempts\" = 3 ]" : "[ \"$result\" = 1 ] && [ \"$attempts\" = 5 ]");
            using var retry = Process.Start(new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", script }, UseShellExecute = false })!;
            await retry.WaitForExitAsync();
            Assert(retry.ExitCode == 0, succeeds ? "transient launchd registration failure retries within one authorization" : "persistent registration failure stops after a bounded retry budget");
        }
    }
}
finally { Directory.Delete(installFixture, true); }
// --- Stability and novice-UX helpers added for Link 1.1 ---
Assert(Ui.Detect("zh-Hans-CN", null) && Ui.Detect("zh-TW", "auto") && !Ui.Detect("en-US", null), "interface language follows a Chinese or other system language");
Assert(!Ui.Detect("zh-CN", "en") && Ui.Detect("en-US", "zh"), "an explicit language choice overrides the system");
Assert(Ui.FirstAppleLanguage("(\n    \"zh-Hans-CN\",\n    \"en-CN\"\n)\n") == "zh-Hans-CN", "macOS preferred language is read from AppleLanguages");
Assert(Ui.Duration(TimeSpan.FromSeconds(75)) == "01:15" && Ui.Duration(TimeSpan.FromMinutes(125)) == "2:05:00", "session time is compact");
var own = OtherVpnClients.OwnMarkers("/Users/u/Library/Application Support/World-Science");
Assert(OtherVpnClients.Detect(OtherVpnClients.ParsePs("/Applications/v2rayN.app/Contents/MacOS/v2rayN\n/sbin/launchd\n"), own) == "v2rayN", "running v2rayN is named before connecting");
Assert(OtherVpnClients.Detect(OtherVpnClients.ParsePs("/Users/u/Library/Application Support/v2rayN/bin/xray/xray\n"), own) == "v2rayN", "an orphaned v2rayN core is attributed to v2rayN");
Assert(OtherVpnClients.Detect(OtherVpnClients.ParsePs("/Users/u/Library/Application Support/World-Science/bin/sing_box/sing-box\n/Library/PrivilegedHelperTools/io.github.robintheai.worldscience.network-501/core\n/Applications/World Science.app/Contents/MacOS/WorldScience\n"), own) is null, "our own cores and helper never count as a conflict");
Assert(OtherVpnClients.Detect([("Clash Verge.exe", @"C:\Program Files\Clash Verge\Clash Verge.exe")], own) == "Clash Verge", "Windows Clash Verge is detected by process name");
Assert(OtherVpnClients.Detect(OtherVpnClients.ParsePs("/Applications/Old Client.app/Contents/MacOS/v2rayNSimple\n"), own) == "Old Client", "an earlier edition on a Mac is named by its app bundle");
Assert(OtherVpnClients.Detect([("v2rayNSimple.exe", @"C:\Apps\Old\v2rayNSimple.exe")], own) == "v2rayN Simple", "an earlier edition on Windows is a separate VPN client");
Assert(OtherVpnClients.Detect(OtherVpnClients.ParsePs("/Library/PrivilegedHelperTools/com.robintheai.old.network-501/core/sing-box\n"), own) == "v2rayN Simple", "an earlier edition's network service tunnel is a conflict, not ours");
Assert(OtherVpnClients.Message("v2rayN").Contains("v2rayN"), "conflict message names the app to quit");
Assert(OtherVpnClients.Detect([("clash-verge-service.exe", @"C:\Program Files\Clash Verge\resources\clash-verge-service.exe")], own) is null, "an always-installed background service alone does not block Connect");
var health = new HealthPolicy();
Assert(health.Record(false, false, true) == HealthAction.Wait && health.Record(false, false, true) == HealthAction.Wait && health.Record(false, false, true) == HealthAction.Reconnect, "three consecutive proxy failures rebuild the tunnel");
Assert(!health.Degraded && health.Interval == HealthPolicy.HealthyInterval, "a rebuild resets the failure count");
health.Record(false, false, true); health.Record(false, false, true);
Assert(health.Record(false, false, false) == HealthAction.Offline && !health.Degraded, "losing Wi-Fi waits instead of switching nodes");
for (var i = 1; i < HealthPolicy.DnsFailureLimit; i++) Assert(health.Record(true, false, true) == HealthAction.Wait, $"slow system DNS alone is tolerated ({i})");
Assert(health.Record(true, false, true) == HealthAction.Reconnect, "persistent system-path failures eventually rebuild");
health.Record(false, true, true);
Assert(health.Degraded && health.Interval == HealthPolicy.DegradedInterval && health.Record(true, true, true) == HealthAction.Healthy && !health.Degraded, "one success clears a degraded state");
Assert(NetworkState.IsRoutable(IPAddress.Parse("192.168.1.8")) && !NetworkState.IsRoutable(IPAddress.Parse("169.254.3.1")) && !NetworkState.IsRoutable(IPAddress.Parse("198.18.0.1")), "link-local and TUN fake addresses do not count as a network");
var at = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
var usage = SubscriptionUsage.Parse("upload=1073741824; download=9663676416; total=107374182400; expire=1801000000", at)!;
Assert(usage.Used == 10737418240 && usage.Remaining == 96636764160 && Math.Abs(usage.UsedFraction!.Value - .1) < 1e-9, "subscription-userinfo traffic is parsed");
Assert(usage.DaysLeft(at) == 11 && !usage.NeedsAttention(at) && usage.Warning(at) is null, "a healthy subscription shows no warning");
Assert(SubscriptionUsage.Parse("upload=0; download=0; total=0; expire=0", at) is { Expire: null, Remaining: null }, "zero expiry means never expires and zero total means unlimited");
Assert(SubscriptionUsage.Parse("upload=95; download=0; total=100", at)!.NeedsAttention(at), "less than 10% data left needs attention");
Assert(SubscriptionUsage.Parse("total=100; expire=1800086400", at)!.Warning(at) is not null && SubscriptionUsage.Parse("total=100; expire=1799999999", at)!.Expired(at), "near and past expiry are flagged");
Assert(SubscriptionUsage.Parse("garbage", at) is null && SubscriptionUsage.Parse(null, at) is null && SubscriptionUsage.Parse("upload=abc;total=1e400", at) is null, "malformed headers are ignored");
Assert(ServiceAvailability.ParseTraceLocation("fl=1\nip=1.2.3.4\nloc=JP\ntls=TLSv1.3\n") == "JP" && ServiceAvailability.ParseTraceLocation("") is null, "Cloudflare trace exit region is parsed");
Assert(ServiceAvailability.RegionVerdict("US") == Availability.Available && ServiceAvailability.RegionVerdict("HK") == Availability.Unavailable && ServiceAvailability.RegionVerdict(null) == Availability.Unknown, "AI services are flagged in unsupported regions only");
Assert(ServiceAvailability.NetflixVerdict(HttpStatusCode.OK, HttpStatusCode.OK) == Availability.Available && ServiceAvailability.NetflixVerdict(HttpStatusCode.NotFound, HttpStatusCode.OK) == Availability.Partial && ServiceAvailability.NetflixVerdict(HttpStatusCode.Forbidden, HttpStatusCode.Forbidden) == Availability.Unavailable, "Netflix catalogue level is classified");
Assert(ServiceAvailability.Flag("JP") == "🇯🇵" && ServiceAvailability.Flag("x") == "🌐", "exit flag is derived from the region code");
var redacted = Diagnostics.Redact("2026-10-29 INFO Simple.Health proxy=False sub=https://sub.example/api?token=abc node=203.0.113.9 user@example.com 127.0.0.1:18890");
Assert(!redacted.Contains("token=abc") && !redacted.Contains("203.0.113.9") && !redacted.Contains("user@example.com") && redacted.Contains("127.0.0.1"), "diagnostics mask links, addresses and emails");
Assert(!Diagnostics.Keep("2026-10-29 INFO Simple.Core: [Error] app/dns: failed for chatgpt.com") && Diagnostics.Keep("2026-10-29 INFO Simple.Health: action=Wait"), "diagnostics drop engine lines that name visited sites");
string[] package = ["WorldScience.exe", "v2rayN.exe", "ServiceLib.dll", "Assets", "bin"];
Assert(ReleaseUpdater.IsDedicatedInstallFolder([("WorldScience.exe", false), ("ServiceLib.dll", false), ("Old.Removed.dll", false), ("Assets", true), ("bin", true)], package), "a normal install folder, including files from older versions, may be replaced");
Assert(!ReleaseUpdater.IsDedicatedInstallFolder([("WorldScience.exe", false), ("ServiceLib.dll", false), ("holiday.jpg", false), ("Assets", true)], package), "Windows update refuses a Downloads folder holding personal files");
Assert(!ReleaseUpdater.IsDedicatedInstallFolder([("WorldScience.exe", false), ("Photos", true), ("Work", true)], package), "Windows update refuses a folder with other people's folders");
Assert(!ReleaseUpdater.IsDedicatedInstallFolder([("WorldScience.exe", false), ("Projects", true)], package) && !ReleaseUpdater.IsDedicatedInstallFolder([("WorldScience.exe", false), ("notes.txt", false)], package), "even one personal folder or note blocks an automatic folder swap");
var taskXml = System.Xml.Linq.XDocument.Parse(LaunchAtLogin.TaskXml(@"C:\Apps\Owlpath & Co\WorldScience.exe", @"PC\user"));
string TaskValue(string name) => taskXml.Descendants().First(e => e.Name.LocalName == name).Value;
Assert(TaskValue("StopIfGoingOnBatteries") == "false" && TaskValue("DisallowStartIfOnBatteries") == "false" && TaskValue("ExecutionTimeLimit") == "PT0S", "Windows login task is never stopped on battery or after 72 hours");
Assert(TaskValue("Command").EndsWith("& Co\\WorldScience.exe") && TaskValue("Arguments") == "--background" && TaskValue("RunLevel") == "HighestAvailable", "Windows login task XML escapes the path and starts elevated in the background");
Assert(LaunchAtLogin.MacBundle("/Applications/Owlpath.app/Contents/MacOS/") == "/Applications/Owlpath.app" && LaunchAtLogin.MacBundle("/tmp/build/") is null, "macOS login item opens the app bundle");
const string clashYaml = """
port: 7890
x-base: &base {server: 192.0.2.10, port: 443}
proxies:
  - {name: "SS 🇯🇵", type: ss, server: 192.0.2.1, port: 8388, cipher: aes-256-gcm, password: "p@ss:w/rd", udp: true}
  - name: VMess WS
    type: vmess
    server: vmess.example.com
    port: 443
    uuid: 00000000-0000-4000-8000-000000000002
    alterId: 0
    cipher: auto
    tls: true
    servername: cdn.example.com
    network: ws
    ws-opts: {path: "/ws?ed=2048", headers: {Host: cdn.example.com}}
  - {name: VMess gRPC, type: vmess, server: 192.0.2.3, port: 443, uuid: 00000000-0000-4000-8000-000000000003, alterId: 0, cipher: auto, tls: true, network: grpc, grpc-opts: {grpc-service-name: svc}}
  - {name: VMess H2, type: vmess, server: 192.0.2.11, port: 443, uuid: 00000000-0000-4000-8000-000000000011, alterId: 0, cipher: auto, tls: true, network: h2, h2-opts: {host: [h2.example.com], path: /h2}}
  - name: VLESS Reality
    type: vless
    server: 192.0.2.4
    port: 443
    uuid: 00000000-0000-4000-8000-000000000004
    flow: xtls-rprx-vision
    tls: true
    servername: www.example.com
    client-fingerprint: chrome
    reality-opts: {public-key: QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVowMTIzNDU, short-id: 6ba85179e30d4fc2}
  - {name: VLESS WS, type: vless, server: 192.0.2.5, port: 443, uuid: 00000000-0000-4000-8000-000000000005, tls: true, network: ws, ws-opts: {path: /vl}, skip-cert-verify: true}
  - {name: Trojan gRPC, type: trojan, server: 192.0.2.6, port: 443, password: "tro jan", sni: t.example.com, network: grpc, grpc-opts: {grpc-service-name: tg}}
  - {name: HY2, type: hysteria2, server: 192.0.2.7, port: 8443, password: hy, sni: h.example.com, obfs: salamander, obfs-password: ob, skip-cert-verify: true}
  - {name: TUIC, type: tuic, server: 192.0.2.8, port: 443, uuid: 00000000-0000-4000-8000-000000000008, password: tu, alpn: [h3], congestion-controller: cubic, sni: tu.example.com}
  - {name: TUIC v4, type: tuic, server: 192.0.2.9, port: 443, token: abc}
  - {name: SSR, type: ssr, server: 192.0.2.12, port: 443, cipher: aes-256-cfb, password: x, obfs: plain, protocol: origin}
  - {name: Snell, type: snell, server: 192.0.2.13, port: 443, psk: x}
  - {name: SS obfs, type: ss, server: 192.0.2.14, port: 443, cipher: aes-128-gcm, password: x, plugin: obfs, plugin-opts: {mode: tls}}
  - {<<: *base, name: Merged, type: trojan, password: m}
proxy-groups:
  - {name: Proxy, type: select, proxies: [VMess WS]}
rules:
  - MATCH,Proxy
""";
var insecureNames = new HashSet<string>();
var clash = SubscriptionFormats.ClashLinks(clashYaml, insecureNames, out var clashSkipped);
Assert(clash.Count == 9 && clashSkipped == 5, "Clash YAML imports supported proxies and skips h2, TUIC v4, SSR, Snell and obfs individually");
Assert(insecureNames.SetEquals(["VLESS WS", "HY2"]), "Clash skip-cert-verify is kept per proxy name");
Uri ClashUri(string scheme, string name) => new(clash.First(l => l.StartsWith(scheme + "://") && Uri.UnescapeDataString(l[(l.IndexOf('#') + 1)..]) == name));
System.Collections.Specialized.NameValueCollection ClashQuery(Uri u) => System.Web.HttpUtility.ParseQueryString(u.Query);
var ssLink = ClashUri("ss", "SS 🇯🇵");
Assert(SubscriptionFormats.Base64(ssLink.UserInfo) == "aes-256-gcm:p@ss:w/rd" && ssLink.Host == "192.0.2.1" && ssLink.Port == 8388, "Clash ss becomes a SIP002 link with cipher, password and emoji name");
var vmessJson = JsonDocument.Parse(clash.Where(l => l.StartsWith("vmess://")).Select(l => SubscriptionFormats.Base64(l[8..]) ?? "").First(j => j.Contains("\"ps\":\"VMess WS\""))).RootElement;
string Vm(string key) => vmessJson.GetProperty(key).GetString()!;
Assert(Vm("net") == "ws" && Vm("host") == "cdn.example.com" && Vm("path") == "/ws?ed=2048" && Vm("tls") == "tls" && Vm("sni") == "cdn.example.com" && Vm("add") == "vmess.example.com" && Vm("port") == "443", "Clash vmess ws+tls becomes a v2rayN vmess link");
Assert(clash.Any(l => l.StartsWith("vmess://") && SubscriptionFormats.Base64(l[8..])!.Contains("\"net\":\"grpc\"") && SubscriptionFormats.Base64(l[8..])!.Contains("\"path\":\"svc\"")), "Clash vmess grpc keeps its service name");
var reality = ClashQuery(ClashUri("vless", "VLESS Reality"));
Assert(reality["security"] == "reality" && reality["pbk"] == "QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVowMTIzNDU" && reality["sid"] == "6ba85179e30d4fc2" && reality["flow"] == "xtls-rprx-vision" && reality["fp"] == "chrome" && reality["sni"] == "www.example.com" && reality["type"] == "tcp" && reality["encryption"] == "none", "Clash vless reality keeps public key, short id, flow and fingerprint");
var vlessWs = ClashQuery(ClashUri("vless", "VLESS WS"));
Assert(vlessWs["security"] == "tls" && vlessWs["type"] == "ws" && vlessWs["path"] == "/vl" && vlessWs["allowInsecure"] == "1", "Clash vless ws+tls keeps path and certificate option");
var trojan = ClashUri("trojan", "Trojan gRPC");
Assert(Uri.UnescapeDataString(trojan.UserInfo) == "tro jan" && ClashQuery(trojan)["type"] == "grpc" && ClashQuery(trojan)["serviceName"] == "tg" && ClashQuery(trojan)["sni"] == "t.example.com" && ClashQuery(trojan)["security"] == "tls", "Clash trojan grpc escapes its password and keeps TLS");
var hy2 = ClashQuery(ClashUri("hysteria2", "HY2"));
Assert(hy2["obfs"] == "salamander" && hy2["obfs-password"] == "ob" && hy2["insecure"] == "1" && hy2["sni"] == "h.example.com", "Clash hysteria2 keeps salamander obfuscation");
var tuic = ClashUri("tuic", "TUIC");
Assert(Uri.UnescapeDataString(tuic.UserInfo) == "00000000-0000-4000-8000-000000000008:tu" && ClashQuery(tuic)["congestion_control"] == "cubic" && ClashQuery(tuic)["alpn"] == "h3", "Clash tuic v5 keeps uuid, password and congestion control");
Assert(ClashUri("trojan", "Merged").Host == "192.0.2.10", "Clash YAML anchors and merge keys are resolved");
Assert(SubscriptionFormats.Prepare(clashYaml).Split('\n').Length == 9 && SubscriptionFormats.Prepare(Convert.ToBase64String(Encoding.UTF8.GetBytes(clashYaml))).Split('\n').Length == 9, "Clash YAML, plain or base64, is prepared as one share link per line");
Assert(SubscriptionFormats.ClashLinks("proxies: [ {name: broken", null, out _).Count == 0 && SubscriptionFormats.Prepare("proxies:\n  - {name: x, type: snell, server: 192.0.2.1, port: 1}") == "", "broken or unsupported-only Clash YAML yields no nodes without throwing");
const string pastedLinks = "vless://00000000-0000-4000-8000-000000000001@192.0.2.20:443?security=tls#A vless://00000000-0000-4000-8000-000000000001@192.0.2.21:443?security=tls#B\r\nss://YWVzLTEyOC1nY206cGFzcw@192.0.2.22:8388#C";
Assert(SubscriptionFormats.Prepare(pastedLinks).Split('\n').Length == 3, "pasted links on one or several lines become one link per line");
var safe64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(pastedLinks + "\n")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
Assert(SubscriptionFormats.Base64(safe64) == pastedLinks + "\n" && SubscriptionFormats.Base64(Convert.ToBase64String(Encoding.UTF8.GetBytes(pastedLinks)).Insert(40, "\n")) == pastedLinks, "URL-safe, unpadded and line-wrapped base64 decode");
Assert(SubscriptionFormats.LooksLikeNodes(pastedLinks) && SubscriptionFormats.LooksLikeNodes(clashYaml) && SubscriptionFormats.LooksLikeNodes(safe64) && SubscriptionFormats.LooksLikeNodes("""{"version": 1, "servers": [{"server": "192.0.2.1", "server_port": 8388, "method": "aes-256-gcm", "password": "x"}]}"""), "pasted links, Clash YAML, base64 and SIP008 are recognised as nodes");
const string sip008 = """{"version": 1, "servers": [{"id": "a", "remarks": "SIP A", "server": "192.0.2.41", "server_port": 8388, "password": "pw", "method": "chacha20-ietf-poly1305"}, {"remarks": "SIP B", "server": "192.0.2.42", "server_port": "8389", "password": "pw2", "method": "aes-256-gcm"}, {"remarks": "SIP plugin", "server": "192.0.2.43", "server_port": 8390, "password": "pw3", "method": "aes-256-gcm", "plugin": "obfs-local"}]}""";
var sipLinks = SubscriptionFormats.Prepare(sip008).Split('\n');
Assert(sipLinks.Length == 2 && SubscriptionFormats.Base64(new Uri(sipLinks[1]).UserInfo) == "aes-256-gcm:pw2" && new Uri(sipLinks[1]).Port == 8389 && Uri.UnescapeDataString(new Uri(sipLinks[0]).Fragment[1..]) == "SIP A", "SIP008 JSON becomes ss links and skips servers that need a plugin");
Assert(SubscriptionFormats.Prepare(safe64).Split('\n').Length == 3 && SubscriptionFormats.Prepare("""[{"server": "192.0.2.44", "server_port": 1, "password": "p", "method": "aes-128-gcm"}]""").StartsWith("ss://"), "URL-safe base64 and bare SIP008 arrays are decoded before import");
Assert(!SubscriptionFormats.LooksLikeNodes("not-a-link") && !SubscriptionFormats.LooksLikeNodes("https://example.com/sub") && !SubscriptionFormats.LooksLikeNodes("<html>Temporarily unavailable</html>") && !SubscriptionFormats.LooksLikeNodes(""), "plain words, web links and HTML are not mistaken for nodes");
Console.WriteLine($"{passed} checks passed");
sealed class Fixture(Func<HttpRequestMessage, HttpResponseMessage> action) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(action(request)); }
}

sealed class InterruptedStream(byte[] bytes) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (Position >= 8) throw new IOException("fixture connection EOF");
        return base.ReadAsync(buffer[..Math.Min(8, buffer.Length)], cancellationToken);
    }
}

sealed class AsyncFixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => action(request, token);
}

sealed class PacedStream(byte[] bytes, FixtureTime time) : MemoryStream(bytes)
{
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        time.Advance(15);
        return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 32_768)], cancellationToken);
    }
}

sealed class FixtureTime : TimeProvider
{
    private long milliseconds;
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => milliseconds;
    public void Advance(long elapsedMilliseconds) => milliseconds += elapsedMilliseconds;
}

sealed class SlowPacedStream(FixtureTime time) : MemoryStream(new byte[8192])
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { time.Advance(500); return base.ReadAsync(buffer[..Math.Min(buffer.Length, 1024)], cancellationToken); }
}
