using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
namespace WorldScience.Services;

/// <summary>Turns Clash/Mihomo YAML and pasted node text into the share links the upstream importer already parses.</summary>
internal static class SubscriptionFormats
{
    internal const int MaxBytes = 8 * 1024 * 1024;
    private static readonly Regex Clash = new(@"(?m)^proxies:", RegexOptions.Compiled);
    private static readonly Regex Share = new(@"(?i)(?<![a-z0-9+.-])(?:vmess|vless|trojan|ss|hysteria2|hy2|tuic|anytls|socks|wireguard|naive\+https|naive\+quic)://\S", RegexOptions.Compiled);
    internal static bool IsClash(string text) => Clash.IsMatch(text);
    private static bool IsSip008(string text) => text.StartsWith('{') && text.Contains("\"servers\"") || text.StartsWith('[') && text.Contains("\"server\"");
    /// <summary>Pasted text that holds nodes itself (links, base64, Clash YAML or SIP008) rather than a subscription address.</summary>
    internal static bool LooksLikeNodes(string text)
    {
        text = text.Trim().TrimStart('\uFEFF');
        if (text.Length == 0) return false;
        if (Share.IsMatch(text) || IsClash(text) || IsSip008(text)) return true;
        return Base64(text) is { } decoded && (Share.IsMatch(decoded) || IsClash(decoded));
    }
    /// <summary>Lenient standard/URL-safe base64, with or without padding or line wrapping; null when it is not base64 text.</summary>
    internal static string? Base64(string text)
    {
        var s = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray()).Replace('-', '+').Replace('_', '/').TrimEnd('=');
        if (s.Length < 4 || s.Length % 4 == 1) return null;
        try { return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='))); }
        catch (Exception ex) when (ex is FormatException or ArgumentException) { return null; }
    }
    /// <summary>Content for <c>ConfigHandler.AddBatchServers</c>. Clash proxies become share links; names that skip certificate checks are collected.</summary>
    internal static string Prepare(string content, ISet<string>? insecure = null)
    {
        var text = content.Trim().TrimStart('\uFEFF');
        if (!IsClash(text) && !Share.IsMatch(text) && !IsSip008(text) && Base64(text)?.Trim() is { } decoded && (IsClash(decoded) || Share.IsMatch(decoded) || IsSip008(decoded))) text = decoded;
        if (IsClash(text)) return string.Join("\n", ClashLinks(text, insecure, out _));
        if (IsSip008(text)) return string.Join("\n", Sip008Links(text));
        if (!Share.IsMatch(text)) return content;
        // Pasted links may share a line; the importer reads one link per line.
        return string.Join("\n", text.Split('\n').Select(l => l.Trim()).SelectMany(l => Share.Matches(l).Count > 1 ? l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) : [l]).Where(l => l.Length > 0));
    }
    internal static List<string> ClashLinks(string yaml, ISet<string>? insecure, out int skipped)
    {
        skipped = 0; var links = new List<string>();
        object? root;
        // Never log parser errors: their context can quote passwords from the document.
        try { root = new DeserializerBuilder().Build().Deserialize(new MergingParser(new Parser(new StringReader(yaml)))); }
        catch (Exception) { return links; }
        if (Get(root, "proxies") is not IList<object> proxies) return links;
        foreach (var proxy in proxies)
        {
            string? link;
            try { link = ClashLink(proxy); } catch (Exception) { link = null; }
            if (link is null) { skipped++; continue; }
            links.Add(link);
            if (insecure is not null && Truthy(Get(proxy, "skip-cert-verify"))) insecure.Add(Str(proxy, "name") ?? "");
        }
        return links;
    }
    /// <summary>Shadowsocks SIP008 JSON (<c>{"servers": [...]}</c> or a bare array). Servers that need a plugin are skipped.</summary>
    internal static List<string> Sip008Links(string json)
    {
        var links = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            foreach (var server in (root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("servers")).EnumerateArray())
            {
                string? Text(string key) => server.TryGetProperty(key, out var v) ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null } : null;
                if (!string.IsNullOrEmpty(Text("plugin")) || !int.TryParse(Text("server_port"), out var port)) continue;
                if (SsLink(Text("server"), port, Text("method"), Text("password"), Text("remarks") ?? "", null) is { } link) links.Add(link);
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
        return links;
    }
    private static string? SsLink(string? server, int port, string? method, string? password, string name, string? plugin)
    {
        if (string.IsNullOrWhiteSpace(server) || port is < 1 or > 65535 || string.IsNullOrEmpty(method) || string.IsNullOrEmpty(password)) return null;
        var user = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{method}:{password}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"ss://{user}@{Host(server)}:{port}" + (plugin is null ? "" : "/?plugin=" + Uri.EscapeDataString(plugin)) + "#" + Uri.EscapeDataString(name);
    }
    private static string Host(string server) => server.Contains(':') && !server.StartsWith('[') ? $"[{server}]" : server;
    private static object? Get(object? map, string key) => map is IDictionary<object, object> d && d.TryGetValue(key, out var v) ? v : null;
    private static string? Str(object? map, string key) => Get(map, key) is string s && s.Length > 0 ? s : null;
    private static bool Truthy(object? v) => v is string s && (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase));
    private static string Query(params (string Key, string? Value)[] items) =>
        string.Join("&", items.Where(i => !string.IsNullOrEmpty(i.Value)).Select(i => i.Key + "=" + Uri.EscapeDataString(i.Value!)));
    /// <summary>One Clash proxy as a share link, or null when the type or a required option is unsupported.</summary>
    private static string? ClashLink(object? p)
    {
        var type = Str(p, "type")?.ToLowerInvariant(); var name = Str(p, "name") ?? ""; var server = Str(p, "server");
        if (server is null || !int.TryParse(Str(p, "port"), out var port) || port is < 1 or > 65535) return null;
        var address = $"{Host(server)}:{port}";
        var remark = "#" + Uri.EscapeDataString(name);
        var sni = Str(p, "servername") ?? Str(p, "sni");
        var alpn = Get(p, "alpn") is IList<object> list ? string.Join(",", list.OfType<string>()) : Str(p, "alpn");
        var fp = Str(p, "client-fingerprint"); var insecure = Truthy(Get(p, "skip-cert-verify")) ? "1" : null;
        var network = Str(p, "network")?.ToLowerInvariant() ?? "tcp";
        var ws = Get(p, "ws-opts"); var wsPath = Str(ws, "path") ?? Str(p, "ws-path") ?? "/"; var wsHost = Str(Get(ws, "headers"), "Host") ?? Str(Get(p, "ws-headers"), "Host");
        var service = Str(Get(p, "grpc-opts"), "grpc-service-name");
        var reality = Get(p, "reality-opts"); var pbk = Str(reality, "public-key");
        // h2 and HTTP-header obfuscation have no equivalent in the desktop cores; those proxies are skipped individually.
        (string Key, string? Value)[] Transport() => network switch
        {
            "ws" => [("type", "ws"), ("host", wsHost), ("path", wsPath)],
            "grpc" => [("type", "grpc"), ("serviceName", service)],
            _ => [("type", "tcp")],
        };
        if (network is not ("tcp" or "ws" or "grpc")) return null;
        string Credential(string key) => Str(p, key) is { } v ? Uri.EscapeDataString(v) : throw new FormatException();
        switch (type)
        {
            case "ss":
                string? plugin = null;
                if (Str(p, "plugin") is { } pluginName)
                {
                    var opts = Get(p, "plugin-opts");
                    if (pluginName != "v2ray-plugin" || (Str(opts, "mode") ?? "websocket") != "websocket") return null;
                    plugin = "v2ray-plugin;mode=websocket" + (Truthy(Get(opts, "tls")) ? ";tls" : "") + (Str(opts, "host") is { } h ? ";host=" + h : "") + (Str(opts, "path") is { } path ? ";path=" + path : "");
                }
                return SsLink(server, port, Str(p, "cipher"), Str(p, "password"), name, plugin);
            case "vmess":
                var vmess = new Dictionary<string, string>
                {
                    ["v"] = "2", ["ps"] = name, ["add"] = server, ["port"] = port.ToString(), ["id"] = Str(p, "uuid") ?? throw new FormatException(),
                    ["aid"] = Str(p, "alterId") ?? "0", ["scy"] = Str(p, "cipher") ?? "auto", ["net"] = network, ["type"] = "none",
                    ["host"] = network == "ws" ? wsHost ?? "" : "", ["path"] = network switch { "ws" => wsPath, "grpc" => service ?? "", _ => "" },
                    ["tls"] = Truthy(Get(p, "tls")) ? "tls" : "", ["sni"] = sni ?? "", ["alpn"] = alpn ?? "", ["fp"] = fp ?? "",
                    ["insecure"] = insecure ?? "", ["allowInsecure"] = insecure ?? "",
                };
                return "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(vmess)));
            case "vless":
                var security = pbk is not null ? "reality" : Truthy(Get(p, "tls")) ? "tls" : "none";
                return $"vless://{Credential("uuid")}@{address}?" + Query([("encryption", Str(p, "encryption") ?? "none"), ("flow", Str(p, "flow")), ("security", security), ("sni", sni), ("alpn", alpn), ("fp", fp),
                    ("pbk", pbk), ("sid", Str(reality, "short-id")), ("allowInsecure", insecure), .. Transport()]) + remark;
            case "trojan":
                return $"trojan://{Credential("password")}@{address}?" + Query([("security", pbk is not null ? "reality" : "tls"), ("sni", sni), ("alpn", alpn), ("fp", fp),
                    ("pbk", pbk), ("sid", Str(reality, "short-id")), ("allowInsecure", insecure), .. Transport()]) + remark;
            case "hysteria2" or "hy2":
                var obfs = Str(p, "obfs");
                if (obfs is not (null or "salamander")) return null;
                return $"hysteria2://{Credential("password")}@{address}?" + Query(("sni", sni), ("alpn", alpn), ("insecure", insecure), ("obfs", obfs), ("obfs-password", obfs is null ? null : Str(p, "obfs-password"))) + remark;
            case "tuic":
                // TUIC v4 (token) is not supported by the cores.
                if (Str(p, "uuid") is null || Str(p, "password") is null) return null;
                return $"tuic://{Credential("uuid")}:{Credential("password")}@{address}?" + Query(("sni", sni), ("alpn", alpn ?? "h3"), ("congestion_control", Str(p, "congestion-controller") ?? "bbr"), ("allow_insecure", insecure)) + remark;
            default:
                return null;
        }
    }
}
