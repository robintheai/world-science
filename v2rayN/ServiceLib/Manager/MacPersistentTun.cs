using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ServiceLib.Manager;

/// <summary>One authorized install; subsequent connections use a fixed-function, UID-authenticated socket.</summary>
public sealed class MacPersistentTun
{
    [DllImport("/usr/lib/libSystem.B.dylib")] private static extern uint getuid();
    [DllImport("/usr/lib/libSystem.B.dylib")] private static extern int getpeereid(int socket, out uint uid, out uint gid);
    private readonly uint _uid = getuid();
    private string Label => $"io.github.robintheai.worldscience.network.{_uid}";
    private string DirectoryPath => $"/Library/PrivilegedHelperTools/io.github.robintheai.worldscience.network-{_uid}";
    private string SocketPath => $"/private/var/run/io.github.robintheai.worldscience.network-{_uid}/control.sock";
    private static string Q(string text) => "'" + text.Replace("'", "'\"'\"'") + "'";
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static async Task<string> HashFile(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }
    public static (byte[] Config, IReadOnlyDictionary<string, string> Resources) PrepareConfiguration(string json, string installedDirectory)
    {
        var document = JsonNode.Parse(json) ?? throw new InvalidOperationException("Invalid network configuration.");
        var resources = new Dictionary<string, string>();
        if (document["route"]?["rule_set"] is JsonArray rules)
            foreach (var rule in rules)
            {
                if (rule?["type"]?.GetValue<string>() != "local" || rule["path"]?.GetValue<string>() is not { } source)
                    throw new InvalidOperationException("The fixed network bridge requires local rules.");
                var file = new FileInfo(source);
                if (!file.Exists || file.Length > 32_000_000) throw new InvalidOperationException("The fixed network rules are missing or invalid.");
                var name = "rule-" + Hash(File.ReadAllBytes(source)) + ".srs";
                resources[name] = source;
                rule["path"] = installedDirectory.TrimEnd('/') + "/" + name;
            }
        return (Encoding.UTF8.GetBytes(document.ToJsonString(new() { WriteIndented = true })), resources);
    }
    public static async Task<string> InstallationCommand(uint uid, string staging, IEnumerable<string> files)
    {
        if (uid < 501 || uid > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(uid));
        var label = $"io.github.robintheai.worldscience.network.{uid}";
        var directory = $"/Library/PrivilegedHelperTools/io.github.robintheai.worldscience.network-{uid}";
        var commands = new StringBuilder("set -eu; umask 077; /bin/mkdir -p /Library/PrivilegedHelperTools; ");
        commands.Append("tmp=$(/usr/bin/mktemp -d /Library/PrivilegedHelperTools/.worldscience-network.XXXXXXXX); trap '/bin/rm -rf \"$tmp\"' EXIT; ");
        foreach (var name in files)
        {
            if (name is not ("helper" or "core" or "config.json" or "dns-on.sh" or "dns-off.sh" or "fingerprint" or "service.plist") &&
                !System.Text.RegularExpressions.Regex.IsMatch(name, "^rule-[a-f0-9]{64}\\.srs$"))
                throw new ArgumentException("Invalid installation resource name.");
            commands.Append($"/bin/cp {Q(Path.Combine(staging, name))} \"$tmp/{name}\"; ");
            commands.Append($"[ \"$(/usr/bin/shasum -a 256 \"$tmp/{name}\" | /usr/bin/awk '{{print $1}}')\" = {Q(await HashFile(Path.Combine(staging, name)))} ] || exit 1; ");
        }
        var targetPlist = $"/Library/LaunchDaemons/{label}.plist";
        commands.Append($"dest={Q(directory)}; plist={Q(targetPlist)}; ");
        commands.Append("[ ! -L \"$dest\" ] && [ ! -L \"$plist\" ] || exit 1; ");
        commands.Append("/usr/sbin/chown -R root:wheel \"$tmp\"; /bin/chmod 755 \"$tmp\"; /bin/chmod 600 \"$tmp\"/*; /bin/chmod 500 \"$tmp/helper\" \"$tmp/core\" \"$tmp/dns-on.sh\" \"$tmp/dns-off.sh\"; /bin/chmod 400 \"$tmp/config.json\"; /bin/chmod 444 \"$tmp/fingerprint\"; /bin/chmod 644 \"$tmp/service.plist\"; ");
        commands.Append("/usr/bin/sed \"s|$dest/|$tmp/|g\" \"$tmp/config.json\" > \"$tmp/check.json\"; \"$tmp/core\" check -c \"$tmp/check.json\" >/dev/null 2>&1; /bin/rm \"$tmp/check.json\"; /usr/bin/plutil -lint \"$tmp/service.plist\" >/dev/null; ");
        // launchd can still be removing the previous job after bootout returns. Wait before
        // replacing its files, and retry registration inside this single authorization.
        commands.Append($"/bin/launchctl bootout system/{label} >/dev/null 2>&1 || true; ");
        commands.Append($"n=0; while /bin/launchctl print system/{label} >/dev/null 2>&1; do n=$((n+1)); [ \"$n\" -lt 50 ] || exit 1; /bin/sleep 0.2; done; ");
        commands.Append($"load_service() {{ n=0; while ! /bin/launchctl bootstrap system \"$plist\"; do /bin/launchctl print system/{label} >/dev/null 2>&1 && return 0; n=$((n+1)); [ \"$n\" -lt 5 ] || return 1; /bin/sleep 1; done; }}; ");
        commands.Append("/bin/rm -rf \"$dest.previous\"; if [ -d \"$dest\" ]; then /bin/mv \"$dest\" \"$dest.previous\"; fi; /bin/mv \"$tmp\" \"$dest\"; ");
        commands.Append("/usr/bin/install -o root -g wheel -m 644 \"$dest/service.plist\" \"$plist\"; ");
        commands.Append("if ! load_service; then /bin/rm -rf \"$dest\"; if [ -d \"$dest.previous\" ]; then /bin/mv \"$dest.previous\" \"$dest\"; /usr/bin/install -o root -g wheel -m 644 \"$dest/service.plist\" \"$plist\"; load_service || true; else /bin/rm -f \"$plist\"; fi; exit 1; fi; /bin/rm -rf \"$dest.previous\"; ");
        return commands.ToString();
    }
    public async Task<string> Command(string command, CancellationToken token = default)
    {
        if (command is not ("START" or "STOP" or "STATUS")) throw new ArgumentException("Unsupported network request.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), timeout.Token);
        if (getpeereid((int)socket.Handle, out var uid, out _) != 0 || uid != 0)
            throw new InvalidOperationException("The network service identity could not be verified.");
        var request = Encoding.ASCII.GetBytes(command + "\n");
        var sent = 0;
        while (sent < request.Length) sent += await socket.SendAsync(request.AsMemory(sent), SocketFlags.None, timeout.Token);
        var response = new byte[128]; var size = 0;
        while (size < response.Length)
        {
            var count = await socket.ReceiveAsync(response.AsMemory(size), SocketFlags.None, timeout.Token);
            if (count == 0) break;
            size += count; if (response[size - 1] == '\n') break;
        }
        var result = Encoding.ASCII.GetString(response, 0, size).Trim();
        if (result == "BUSY") throw new InvalidOperationException("Another World Science window is using the network service. Disconnect it first.");
        if (!result.StartsWith("OK", StringComparison.Ordinal)) throw new InvalidOperationException("The network service could not complete the request. Reopen World Science and try again.");
        return result;
    }
    public async Task<int> Connect(string core, string config, MacAuthorizationPromptGate gate,
        Func<string, CancellationToken, Task<string>> authorize, CancellationToken token, Action<string>? diagnostic = null)
    {
        if (_uid < 501) throw new InvalidOperationException("Open World Science from your normal macOS user account.");
        var helper = Path.Combine(AppContext.BaseDirectory, "Assets", "network-helper");
        var versionFile = helper + ".version";
        if (!File.Exists(helper) || !File.Exists(versionFile)) throw new InvalidOperationException("The network helper is missing. Reinstall World Science.");
        var sourceVersion = (await File.ReadAllTextAsync(versionFile, token)).Trim();
        if (sourceVersion.Length != 64 || sourceVersion.Any(c => !Uri.IsHexDigit(c))) throw new InvalidOperationException("Invalid network helper version.");
        var coreHash = await HashFile(core);
        var prepared = PrepareConfiguration(await File.ReadAllTextAsync(config, token), DirectoryPath);
        var configBytes = prepared.Config;
        var fingerprint = Hash(Encoding.UTF8.GetBytes("network-helper-1\n" + sourceVersion + "\n" + coreHash + "\n" + Hash(configBytes)));
        var manifest = Path.Combine(DirectoryPath, "fingerprint");
        var installed = File.Exists(manifest) ? (await File.ReadAllTextAsync(manifest, token)).Trim() : "";
        diagnostic?.Invoke($"Network service: installed={installed[..Math.Min(12, installed.Length)]}; expected={fingerprint[..12]}; reuse={installed == fingerprint}");
        if (installed != fingerprint)
        {
            if (!gate.TryConsume()) throw new MacNetworkAuthorizationException("Network setup needs permission. Click Connect to finish setup.");
            // The OS asks once to install/upgrade this fixed service. No credentials enter World Science.
            var staging = Path.Combine(Path.GetTempPath(), "worldscience-network-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                File.Copy(helper, Path.Combine(staging, "helper"));
                File.Copy(core, Path.Combine(staging, "core"));
                if (await HashFile(Path.Combine(staging, "core")) != coreHash) throw new InvalidOperationException("Network core changed during setup. Try again.");
                foreach (var resource in prepared.Resources)
                {
                    var destination = Path.Combine(staging, resource.Key); File.Copy(resource.Value, destination);
                    if ("rule-" + await HashFile(destination) + ".srs" != resource.Key) throw new InvalidOperationException("Network rules changed during setup. Try again.");
                }
                await File.WriteAllBytesAsync(Path.Combine(staging, "config.json"), configBytes, token);
                var owner = Hash(Encoding.UTF8.GetBytes(Label))[..32];
                var resolver = MacTunDns.ResolverAddress(Encoding.UTF8.GetString(configBytes)) ?? throw new InvalidOperationException("Missing fixed network resolver.");
                await File.WriteAllTextAsync(Path.Combine(staging, "dns-on.sh"), "#!/bin/sh\nset -eu\n" + MacTunDns.Install(owner, resolver) + "\n" + MacTunDns.Flush + "\n", token);
                await File.WriteAllTextAsync(Path.Combine(staging, "dns-off.sh"), "#!/bin/sh\n" + MacTunDns.Remove(owner) + "\n" + MacTunDns.Flush + "\n", token);
                await File.WriteAllTextAsync(Path.Combine(staging, "fingerprint"), fingerprint, token);
                var plist = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\"><plist version=\"1.0\"><dict><key>Label</key><string>{Label}</string><key>ProgramArguments</key><array><string>{DirectoryPath}/helper</string><string>{_uid}</string></array><key>RunAtLoad</key><true/><key>KeepAlive</key><true/><key>ThrottleInterval</key><integer>2</integer><key>ProcessType</key><string>Background</string><key>StandardOutPath</key><string>/dev/null</string><key>StandardErrorPath</key><string>/dev/null</string></dict></plist>";
                await File.WriteAllTextAsync(Path.Combine(staging, "service.plist"), plist, token);
                var files = new[] { "helper", "core", "config.json", "dns-on.sh", "dns-off.sh", "fingerprint", "service.plist" }.Concat(prepared.Resources.Keys);
                await authorize(await InstallationCommand(_uid, staging, files), token);
            }
            finally { Directory.Delete(staging, true); }
        }
        string? status = null;
        for (var i = 0; i < 30; i++)
        {
            token.ThrowIfCancellationRequested();
            try { status = await Command("STATUS", token); break; }
            catch (SocketException) { await Task.Delay(200, token); }
        }
        if (status is null) throw new InvalidOperationException("The installed network service is unavailable. Check World Science in macOS Login Items & Extensions, or reinstall World Science.");
        var fields = status.Split(' ');
        if (fields.Length != 4 || fields[1] != "1" || !int.TryParse(fields[2], out var pid) || pid <= 1) throw new InvalidOperationException("Unsupported network service response.");
        await Command("START", token);
        return pid;
    }
}
