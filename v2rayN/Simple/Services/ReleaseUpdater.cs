using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
namespace WorldScience.Services;
public record ReleaseAsset(string Name, string Url, string? Digest);
public record SimpleRelease(Version Version, string Notes, string Page, ReleaseAsset Package, ReleaseAsset Checksums);
public sealed class ReleaseUpdater(HttpClient http)
{
    public const string Repository = "robintheai/world-science";
    // The website mirror is tried first: GitHub is often unreachable in mainland China before a proxy is running.
    public static readonly string[] Feeds = ["https://ai.z5a.com/clients/world-science/latest.json", $"https://api.github.com/repos/{Repository}/releases/latest"];
    public static Version CurrentVersion => typeof(ReleaseUpdater).Assembly.GetName().Version ?? new Version(1, 0, 0);
    public static string AssetName(string os, string arch, Version version) => $"World-Science-{version.ToString(3)}-{os}-{arch}.{(os == "macos" ? "dmg" : "zip")}";
    public async Task<SimpleRelease?> Check(Version current, string os, string arch, CancellationToken ct = default)
    {
        for (var i = 0; ; i++)
        {
            try { return await Check(Feeds[i], current, os, arch, ct); }
            catch (Exception ex) when (i < Feeds.Length - 1 && !ct.IsCancellationRequested && ex is HttpRequestException or JsonException or KeyNotFoundException or OperationCanceledException) { }
        }
    }
    private async Task<SimpleRelease?> Check(string feed, Version current, string os, string arch, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, feed);
        request.Headers.UserAgent.ParseAdd("WorldScience/1.0");
        request.Headers.Accept.ParseAdd("application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await http.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var root = doc.RootElement;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean() || root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (!Version.TryParse((root.TryGetProperty("tag_name", out var tag) ? tag.GetString() : root.GetProperty("version").GetString())?.TrimStart('v'), out var version) || version <= current) return null;
        var expected = AssetName(os, arch, version);
        var assets = root.GetProperty("assets").EnumerateArray().Select(a => new ReleaseAsset(a.GetProperty("name").GetString()!, a.GetProperty("browser_download_url").GetString()!, a.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String ? digest.GetString() : null)).ToList();
        var package = assets.FirstOrDefault(a => a.Name == expected) ?? throw new InvalidOperationException(Ui.T("The new version has no package for this computer yet. Try again later.", "新版尚未提供本机架构的安装包，请稍后重试。"));
        var checksums = assets.FirstOrDefault(a => a.Name == "SHA256SUMS") ?? throw new InvalidOperationException(Ui.T("The new version is missing its checksum file. Try again later.", "新版缺少完整性校验文件，请稍后重试。"));
        EnsureAssetUrl(package.Url); EnsureAssetUrl(checksums.Url);
        return new(version, root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "", root.GetProperty("html_url").GetString()!, package, checksums);
    }
    private const string AssetFile = @"v\d+\.\d+\.\d+/(World-Science-\d+\.\d+\.\d+-(macos|windows)-(arm64|x64)\.(dmg|zip)|SHA256SUMS)$";
    /// <summary>Packages come only from this project's GitHub releases or its website mirror; SHA256SUMS is verified either way.</summary>
    public static void EnsureAssetUrl(string url)
    {
        var uri = new Uri(url);
        var path = uri.Host switch
        {
            "ai.z5a.com" => "^/clients/world-science/releases/",
            "github.com" => $"^/{Repository}/releases/download/",
            _ => null
        };
        if (path is null || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            !System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath, path + AssetFile))
            throw new InvalidOperationException(Ui.T("The update package does not come from this project's release channel.", "更新安装包地址不属于本项目发布渠道"));
    }
    public async Task<string> Download(SimpleRelease release, string directory, IProgress<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        EnsureAssetUrl(release.Package.Url); EnsureAssetUrl(release.Checksums.Url);
        string sums = "";
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, release.Checksums.Url) { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
                using var response = await http.SendAsync(request, ct); response.EnsureSuccessStatusCode();
                sums = await response.Content.ReadAsStringAsync(ct); break;
            }
            catch (HttpRequestException) when (attempt < 2) { await Task.Delay(TimeSpan.FromSeconds(attempt + 1), ct); }
        }
        var expected = sums.Split('\n').Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(a => a.Length == 2 && a[1].TrimStart('*') == release.Package.Name)?[0];
        if (expected is null || expected.Length != 64 || !expected.All(Uri.IsHexDigit)) throw new InvalidOperationException("校验清单中没有匹配的安装包");
        var output = Path.Combine(directory, release.Package.Name);
        var temporary = output + ".part";
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var offset = File.Exists(temporary) ? new FileInfo(temporary).Length : 0;
                    using var request = new HttpRequestMessage(HttpMethod.Get, release.Package.Url) { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
                    if (offset > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, null);
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) { File.Delete(temporary); throw new IOException("需要重新下载"); }
                    response.EnsureSuccessStatusCode();
                    var resume = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.From == offset;
                    if (response.StatusCode == HttpStatusCode.PartialContent && !resume) throw new IOException("下载范围异常");
                    if (!resume) offset = 0;
                    var total = response.Content.Headers.ContentRange?.Length ?? (response.Content.Headers.ContentLength is long size ? offset + size : (long?)null);
                    await using var source = await response.Content.ReadAsStreamAsync(ct);
                    await using var file = new FileStream(temporary, resume ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
                    long received = offset; var buffer = new byte[128 * 1024];
                    while (true)
                    {
                        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct); idle.CancelAfter(TimeSpan.FromSeconds(30));
                        var count = await source.ReadAsync(buffer, idle.Token); if (count == 0) break;
                        await file.WriteAsync(buffer.AsMemory(0, count), ct); received += count;
                        progress.Report(total > 0 ? received * 100d / total.Value : 0);
                    }
                    if (total.HasValue && received != total.Value) throw new IOException("下载中断");
                    break;
                }
                catch (Exception ex) when (attempt < 3 && !ct.IsCancellationRequested && (ex is IOException or HttpRequestException or OperationCanceledException))
                { await Task.Delay(TimeSpan.FromSeconds(attempt + 1), ct); }
            }
            await using var verify = File.OpenRead(temporary);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(verify, ct));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("安装包完整性校验失败，请重新下载。");
            verify.Close();
            File.Move(temporary, output, true);
            progress.Report(100);
            return output;
        }
        catch { if (File.Exists(temporary)) File.Delete(temporary); throw; }
    }
    public static string BackupConfiguration(string root)
    {
        var target = Path.Combine(root, "guiBackups", $"simple-before-update-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var archive = ZipFile.Open(target, ZipArchiveMode.Create);
        var config = Path.Combine(root, "guiConfigs");
        if (Directory.Exists(config)) foreach (var file in Directory.EnumerateFiles(config, "*", SearchOption.AllDirectories))
            archive.CreateEntryFromFile(file, Path.Combine("guiConfigs", Path.GetRelativePath(config, file)), CompressionLevel.Optimal);
        var settings = Path.Combine(root, "simple-settings.json");
        if (File.Exists(settings)) archive.CreateEntryFromFile(settings, "simple-settings.json");
        return target;
    }
    // Only file types an older build of this app may have shipped. Documents and images are never assumed to be ours.
    private static readonly string[] AppFileExtensions = [".dll", ".exe", ".pdb", ".json", ".config", ".dylib", ".so"];
    /// <summary>
    /// The Windows ZIP has no top-level folder, so users who "extract here" into Downloads or Desktop run
    /// the app from a folder full of their own files. Swapping that folder would move all of them.
    /// Only a folder that holds nothing but this app's files (current or from an older version) may be replaced.
    /// </summary>
    public static bool IsDedicatedInstallFolder(IEnumerable<(string Name, bool Directory)> installed, IReadOnlyCollection<string> package)
    {
        var known = new HashSet<string>(package, StringComparer.OrdinalIgnoreCase);
        if (!known.Contains("WorldScience.exe")) return false;
        foreach (var (name, directory) in installed)
        {
            if (known.Contains(name)) continue;
            if (directory || !AppFileExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
    /// <summary>
    /// Removes staging folders and rollback copies left by earlier updates. A rollback copy is deleted only when
    /// it contains nothing but app files, so an unexpected personal file is never destroyed.
    /// </summary>
    public static void CleanupWindowsUpdateLeftovers(string installPath)
    {
        try
        {
            var install = new DirectoryInfo(installPath.TrimEnd(Path.DirectorySeparatorChar));
            if (install.Parent is not { } parent) return;
            var current = install.EnumerateFileSystemInfos().Select(e => e.Name).ToList();
            foreach (var leftover in parent.EnumerateDirectories(".simple-*"))
            {
                var stage = leftover.Name.StartsWith(".simple-stage-", StringComparison.Ordinal);
                var previous = leftover.Name.StartsWith(".simple-previous-", StringComparison.Ordinal);
                if (!stage && !previous) continue;
                if (previous && !IsDedicatedInstallFolder(leftover.EnumerateFileSystemInfos().Select(e => (e.Name, e is DirectoryInfo)), current)) continue;
                try { leftover.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception) { /* Best effort; never block startup. */ }
    }
    public static void StartWindowsUpgrade(string package, string installPath)
    {
        var parent = Directory.GetParent(installPath.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
        var stage = Path.Combine(parent, ".simple-stage-" + Guid.NewGuid().ToString("N"));
        ZipFile.ExtractToDirectory(package, stage);
        if (!File.Exists(Path.Combine(stage, "WorldScience.exe"))) { Directory.Delete(stage, true); throw new InvalidOperationException(Ui.T("The update package is invalid.", "安装包结构不正确。")); }
        var installed = new DirectoryInfo(installPath).EnumerateFileSystemInfos().Select(e => (e.Name, e is DirectoryInfo)).ToList();
        var packaged = new DirectoryInfo(stage).EnumerateFileSystemInfos().Select(e => e.Name).ToList();
        if (!IsDedicatedInstallFolder(installed, packaged))
        {
            Directory.Delete(stage, true);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", package } });
            throw new InvalidOperationException(Ui.T(
                "World Science shares its folder with other files, so it cannot update itself safely. Extract the downloaded ZIP into its own folder.",
                "World Science 所在的文件夹里还有其他文件，无法安全自动更新。请把下载好的压缩包解压到一个单独的新文件夹再打开。"));
        }
        var backup = Path.Combine(parent, ".simple-previous-" + Guid.NewGuid().ToString("N"));
        static string Q(string value) => "'" + value.Replace("'", "''") + "'";
        var script = $"$ErrorActionPreference='Stop'\nWait-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue\nStart-Sleep -Seconds 2\n" +
            $"$dst={Q(installPath)}; $stage={Q(stage)}; $old={Q(backup)}\n" +
            "try { Move-Item -LiteralPath $dst -Destination $old; Move-Item -LiteralPath $stage -Destination $dst; Start-Process -FilePath (Join-Path $dst 'WorldScience.exe') } catch { if ((Test-Path $old) -and !(Test-Path $dst)) { Move-Item -LiteralPath $old -Destination $dst }; Add-Type -AssemblyName PresentationFramework; [System.Windows.MessageBox]::Show('更新失败，已保留旧版。请手动解压安装。') }";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        Process.Start(new ProcessStartInfo("powershell.exe") { Arguments = "-NoProfile -NonInteractive -EncodedCommand " + encoded, UseShellExecute = false, CreateNoWindow = true });
    }
}
