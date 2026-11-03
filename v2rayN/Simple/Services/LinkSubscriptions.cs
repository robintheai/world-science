using ServiceLib.Helper;
using System.Security.Cryptography;
using System.Text;
namespace WorldScience.Services;

/// <summary>Download locally and validate a staged catalog before atomically replacing saved routes.</summary>
internal static class LinkSubscriptions
{
    private const string StagingName = "__world_science_pending__";
    internal static Uri ParseUrl(string text)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ||
            string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException(Ui.T("Enter a complete HTTP or HTTPS subscription link, or paste node links.", "请输入完整的 HTTP 或 HTTPS 订阅链接，或粘贴节点链接。"));
        return uri;
    }
    /// <summary>Pasted nodes are saved as a local subscription with no address; it is never downloaded or refreshed.</summary>
    internal static bool IsManual(SubItem subscription) => string.IsNullOrWhiteSpace(subscription.Url);
    private static string TooLarge => Ui.T("This subscription is too large to import.", "订阅内容过大，无法导入。");
    /// <returns>False when the same text was already saved.</returns>
    internal static async Task<bool> ImportText(Config config, string text, CancellationToken token)
    {
        if (Encoding.UTF8.GetByteCount(text) > SubscriptionFormats.MaxBytes) throw new InvalidOperationException(TooLarge);
        var memo = "manual:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Trim()))).ToLowerInvariant();
        var subs = await AppManager.Instance.SubItems() ?? [];
        if (subs.Any(s => s.Enabled && s.Memo == memo)) return false;
        var name = Ui.T("Manual nodes", "手动添加的节点");
        for (var n = 2; subs.Any(s => s.Remarks == name); n++) name = Ui.T("Manual nodes", "手动添加的节点") + " " + n;
        await Replace(config, new SubItem { Id = Guid.NewGuid().ToString("N"), Remarks = name, Url = "", Memo = memo }, text, "", token);
        return true;
    }
    internal static async Task CleanPending(Config config)
    {
        foreach (var sub in (await AppManager.Instance.SubItems() ?? []).Where(s => !s.Enabled && s.Remarks == StagingName))
            await ConfigHandler.DeleteSubItem(config, sub.Id);
    }
    /// <returns>Traffic/expiry advertised by the provider, when it sends <c>subscription-userinfo</c>.</returns>
    internal static async Task<SubscriptionUsage?> Refresh(Config config, SubItem subscription, int? proxyPort, CancellationToken token)
    {
        var uri = ParseUrl(subscription.Url);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        SubscriptionUsage? usage = null;
        async Task<string> Download(int? route)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            attempt.CancelAfter(TimeSpan.FromSeconds(route.HasValue ? 12 : 18));
            using var handler = new SocketsHttpHandler { UseProxy = route.HasValue, Proxy = route.HasValue ? new WebProxy($"socks5://127.0.0.1:{route}") : null, ConnectTimeout = TimeSpan.FromSeconds(8), AllowAutoRedirect = true, MaxAutomaticRedirections = 5 };
            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = SubscriptionFormats.MaxBytes };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("v2rayN/7.0 WorldScience/1.0");
            // Do not log the URL, subscription payload or HTTP exception: each can contain credentials.
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, attempt.Token);
            response.EnsureSuccessStatusCode();
            if (response.Headers.TryGetValues("subscription-userinfo", out var info) || response.Content.Headers.TryGetValues("subscription-userinfo", out info))
                usage = SubscriptionUsage.Parse(string.Join(";", info), DateTimeOffset.UtcNow);
            if (response.Content.Headers.ContentLength is > SubscriptionFormats.MaxBytes) throw new InvalidOperationException(TooLarge);
            await using var stream = await response.Content.ReadAsStreamAsync(attempt.Token);
            using var bytes = new MemoryStream(); var buffer = new byte[32768];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, attempt.Token); if (read == 0) break;
                if (bytes.Length + read > SubscriptionFormats.MaxBytes) throw new InvalidOperationException(TooLarge);
                bytes.Write(buffer, 0, read);
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }
        string content;
        try
        {
            try { content = await Download(proxyPort); }
            catch (Exception ex) when (proxyPort.HasValue && !budget.IsCancellationRequested && ex is HttpRequestException or IOException or OperationCanceledException)
            { content = await Download(null); }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new SubscriptionUnreachableException(Ui.T("The subscription timed out. Check the link and try again.", "订阅下载超时，请检查链接后重试。")); }
        catch (HttpRequestException ex)
        {
            var message = Ui.T("Unable to download this subscription. Check the link and try again.", "订阅下载失败，请检查链接后重试。");
            // A status code means the provider answered and refused; no status means the network never reached it.
            throw ex.StatusCode is null ? new SubscriptionUnreachableException(message) : new InvalidOperationException(message);
        }
        await Replace(config, subscription, content, uri.AbsoluteUri, budget.Token);
        return usage;
    }
    /// <summary>Parses into a disabled staging subscription, then swaps rows in one transaction; any failure keeps saved nodes.</summary>
    private static async Task Replace(Config config, SubItem subscription, string content, string url, CancellationToken token)
    {
        var insecure = new HashSet<string>();
        content = SubscriptionFormats.Prepare(content, insecure);
        if (string.IsNullOrWhiteSpace(content)) throw new InvalidOperationException(NoLocations);
        var stage = new SubItem { Id = Guid.NewGuid().ToString("N"), Remarks = StagingName, Url = "", Enabled = false };
        await ConfigHandler.AddSubItem(config, stage);
        try
        {
            await ConfigHandler.AddBatchServers(config, content, stage.Id, true);
            var rows = (await AppManager.Instance.ProfileItems(stage.Id) ?? [])
                .Where(p => p.IsValid() && p.ConfigType is not (EConfigType.Custom or EConfigType.Outbound) && !SubscriptionLocations.IsNotice(p.Remarks)).ToList();
            if (rows.Count == 0) throw new InvalidOperationException(NoLocations);
            // Share links cannot carry every TLS option; Clash's skip-cert-verify is applied by proxy name.
            foreach (var row in rows.Where(r => insecure.Contains(r.Remarks))) row.AllowInsecure = Global.StringTrue;
            var previous = (await AppManager.Instance.ProfileItems(subscription.Id) ?? [])
                .GroupBy(p => ClientController.MeasurementKey(new NodeRow(p))).ToDictionary(g => g.Key, g => g.First().IndexId);
            var keys = new HashSet<string>();
            rows = rows.Where(p => { p.Subid = subscription.Id; return keys.Add(ClientController.MeasurementKey(new NodeRow(p))); }).ToList();
            foreach (var row in rows)
                if (previous.TryGetValue(ClientController.MeasurementKey(new NodeRow(row)), out var id)) row.IndexId = id;
            token.ThrowIfCancellationRequested();
            subscription.Enabled = true; subscription.Url = url;
            await SQLiteHelper.Instance.RunInTransactionAsync(db =>
            {
                db.Execute("DELETE FROM ProfileItem WHERE Subid = ?", subscription.Id);
                db.Execute("DELETE FROM ProfileItem WHERE Subid = ?", stage.Id);
                db.InsertOrReplace(subscription);
                foreach (var row in rows) db.Insert(row);
                db.Delete<SubItem>(stage.Id);
            });
        }
        finally { await ConfigHandler.DeleteSubItem(config, stage.Id); }
    }
    private static string NoLocations => Ui.T("No supported locations found. Use a subscription link, node links or a Clash config.", "没有找到可用的线路，请使用订阅链接、节点链接或 Clash 配置。");
}
/// <summary>The provider could not be reached (offline, timeout), as opposed to answering with unusable content.</summary>
internal sealed class SubscriptionUnreachableException(string message) : InvalidOperationException(message);
