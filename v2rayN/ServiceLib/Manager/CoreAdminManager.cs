using CliWrap;
using CliWrap.Buffered;

namespace ServiceLib.Manager;

public class CoreAdminManager
{
    private static readonly Lazy<CoreAdminManager> _instance = new(() => new());
    public static CoreAdminManager Instance => _instance.Value;
    private Config _config;
    private Func<bool, string, Task>? _updateFunc;
    private int _linuxSudoPid = -1;
    private string? _macStopSignal;
    private MacPersistentTun? _persistentTun;
    public bool MacTunSuspended { get; private set; }
    public async Task SetMacTunSuspended(bool suspended)
    {
        if (_persistentTun is not null)
        {
            await _persistentTun.Command(suspended ? "STOP" : "START");
            MacTunSuspended = suspended; return;
        }
        var marker = _macStopSignal ?? throw new InvalidOperationException("No authorized network session is available.");
        if (!File.Exists(marker)) throw new InvalidOperationException("Network session ended. Connect again.");
        var pending = marker + ".pending";
        await File.WriteAllTextAsync(pending, suspended ? "idle" : "active");
        File.Move(pending, marker, true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (Directory.Exists(marker + ".idle") != suspended)
        {
            try { if (Process.GetProcessById(_linuxSudoPid).HasExited) throw new InvalidOperationException("Network session ended. Connect again."); }
            catch (ArgumentException) { throw new InvalidOperationException("Network session ended. Connect again."); }
            await Task.Delay(50, timeout.Token);
        }
        MacTunSuspended = suspended;
    }
    // Null preserves upstream behavior; Simple supplies an explicit user-action gate.
    public MacAuthorizationPromptGate? MacPromptGate { get; set; }
    private CancellationTokenSource? _macAuthorization;
    public void CancelMacAuthorization()
    {
        try { _macAuthorization?.Cancel(); } catch (ObjectDisposedException) { }
    }
    private const string _tag = "CoreAdminHandler";

    public async Task Init(Config config, Func<bool, string, Task> updateFunc)
    {
        if (_config != null)
        {
            return;
        }
        _config = config;
        _updateFunc = updateFunc;

        await Task.CompletedTask;
    }

    private async Task UpdateFunc(bool notify, string msg)
    {
        await _updateFunc?.Invoke(notify, msg);
    }

    public async Task<ProcessService?> RunProcessAsLinuxSudo(string fileName, CoreInfo coreInfo, string configPath)
    {
        if (Utils.IsMacOS()) return await RunMacAuthorized(fileName, coreInfo, configPath);
        StringBuilder sb = new();
        sb.AppendLine("#!/bin/bash");
        var cmdLine = $"{fileName.AppendQuotes()} {string.Format(coreInfo.Arguments, Utils.GetBinConfigPath(configPath).AppendQuotes())}";

        // Passing environment variables to the sudo command, here it only xray or sing-box.
        if (coreInfo.Environment.Count > 0)
        {
            var envArgs = string.Join(" ", coreInfo.Environment.Where(kv => kv.Value.IsNotEmpty()).Select(kv => $"{kv.Key}={kv.Value.AppendQuotes()}"));
            sb.AppendLine($"exec sudo -S -- env {envArgs} {cmdLine}");
        }
        else
        {
            sb.AppendLine($"exec sudo -S -- {cmdLine}");
        }

        var shFilePath = await FileUtils.CreateLinuxShellFile("run_as_sudo.sh", sb.ToString(), true);

        var procService = new ProcessService(
            fileName: shFilePath,
            arguments: "",
            workingDirectory: Utils.GetBinConfigPath(),
            displayLog: true,
            redirectInput: true,
            environmentVars: null,
            updateFunc: _updateFunc
        );

        await procService.StartAsync(AppManager.Instance.LinuxSudoPwd);

        if (procService is null or { HasExited: true })
        {
            throw new Exception(ResUI.FailedToRunCore);
        }
        _linuxSudoPid = procService.Id;

        return procService;
    }

    public async Task KillProcessAsLinuxSudo()
    {
        MacTunSuspended = false;
        if (_persistentTun is not null)
        {
            var helper = _persistentTun; _persistentTun = null; _linuxSudoPid = -1;
            try { await helper.Command("STOP"); }
            catch (Exception ex) { Logging.SaveLog(_tag, ex); }
            return;
        }
        if (_linuxSudoPid < 0)
        {
            return;
        }

        try
        {
            if (Utils.IsMacOS())
            {
                if (_macStopSignal is not null)
                {
                    File.Delete(_macStopSignal); _macStopSignal = null;
                    for (var i = 0; i < 30; i++)
                    {
                        try { if (Process.GetProcessById(_linuxSudoPid).HasExited) break; }
                        catch (ArgumentException) { break; }
                        await Task.Delay(100);
                    }
                }
                else if (MacPromptGate is null) await RunMacShell($"/bin/kill -TERM {_linuxSudoPid} 2>/dev/null || true");
                // Cleanup and background recovery must never request a fresh password.
                _linuxSudoPid = -1;
                await Task.Delay(300);
                return;
            }
            var shellFileName = Utils.IsMacOS() ? Global.KillAsSudoOSXShellFileName : Global.KillAsSudoLinuxShellFileName;
            var shFilePath = await FileUtils.CreateLinuxShellFile("kill_as_sudo.sh", EmbedUtils.GetEmbedText(shellFileName), true);
            if (shFilePath.Contains(' '))
            {
                shFilePath = shFilePath.AppendQuotes();
            }
            var arg = new List<string>() { "-c", $"sudo -S {shFilePath} {_linuxSudoPid}" };
            var result = await Cli.Wrap(Global.LinuxBash)
                .WithArguments(arg)
                .WithStandardInputPipe(PipeSource.FromString(AppManager.Instance.LinuxSudoPwd))
                .ExecuteBufferedAsync();

            await UpdateFunc(false, result.StandardOutput.ToString());

            await Task.Delay(1000); // Wait for a second to ensure the process is killed
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }

        _linuxSudoPid = -1;
    }
    private static string ShellQuote(string text) => "'" + text.Replace("'", "'\"'\"'") + "'";

    private static async Task<string> RunMacShell(string command, CancellationToken token = default)
    {
        // The credential prompt belongs to macOS; no password enters the client or logs.
        var script = "do shell script " + System.Text.Json.JsonSerializer.Serialize(command,
            new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + " with administrator privileges";
        var result = await Cli.Wrap("/usr/bin/osascript").WithArguments(new[] { "-e", script }).ExecuteBufferedAsync(token);
        return result.StandardOutput.Trim();
    }

    private async Task<ProcessService?> RunMacAuthorized(string fileName, CoreInfo info, string configPath)
    {
        if (MacPromptGate is not null && info.CoreType == ECoreType.sing_box)
        {
            var helper = new MacPersistentTun();
            using var setup = new CancellationTokenSource(); _macAuthorization = setup;
            try
            {
                _linuxSudoPid = await helper.Connect(fileName, Utils.GetBinConfigPath(configPath), MacPromptGate, RunMacShell, setup.Token, message => Logging.SaveLog("Simple.Helper: " + message));
                _persistentTun = helper; MacTunSuspended = false;
                return new ProcessService(_linuxSudoPid, _updateFunc, monitorOnly: true);
            }
            catch (Exception ex)
            {
                // A cancelled START may have reached the helper before the socket closed.
                try { await helper.Command("STOP"); } catch { }
                if (ex is InvalidOperationException) throw;
                throw new MacNetworkAuthorizationException("Network setup was not completed. Click Connect when you are ready.", ex);
            }
            finally { _macAuthorization = null; }
        }
        if (MacPromptGate is not null && !MacPromptGate.TryConsume())
            throw new MacNetworkAuthorizationException("Network access needs permission. Click Connect to try again.");
        var arguments = string.Format(info.Arguments, ShellQuote(Utils.GetBinConfigPath(configPath)));
        var env = string.Join(" ", info.Environment.Select(kv => kv.Key + "=" + ShellQuote(string.Format(kv.Value, Utils.GetBinConfigPath(configPath)))));
        var logPath = Utils.GetLogPath("simple-tun.log");
        _macStopSignal = Utils.GetTempPath("simple-tun-" + Guid.NewGuid().ToString("N") + ".active");
        await File.WriteAllTextAsync(_macStopSignal, "active");
        var dnsAddress = info.CoreType == ECoreType.sing_box
            ? MacTunDns.ResolverAddress(await File.ReadAllTextAsync(Utils.GetBinConfigPath(configPath))) : null;
        var dnsOwner = Guid.NewGuid().ToString("N");
        var cleanupDns = dnsAddress is null ? "true" : MacTunDns.Remove(dnsOwner) + "; " + MacTunDns.Flush;
        // The privileged watcher also observes core death, so a crash cannot leave a dead resolver behind.
        var cleanup = cleanupDns + "; /bin/kill -TERM \"$1\" 2>/dev/null || true";
        var watcher = $"trap {ShellQuote(cleanup)} EXIT; trap 'exit 0' TERM INT; while /bin/kill -0 {Environment.ProcessId} 2>/dev/null && /bin/kill -0 \"$1\" 2>/dev/null && /bin/test -f {ShellQuote(_macStopSignal)}; do /bin/sleep 0.25; done";
        var installDns = dnsAddress is null ? "" :
            $"/bin/sleep 0.4; if ! ( {MacTunDns.Install(dnsOwner, dnsAddress)} ); then {MacTunDns.Remove(dnsOwner)}; /bin/kill -TERM \"$child\"; exit 1; fi; {MacTunDns.Flush}; ";
        var command = $"cd {ShellQuote(Utils.GetBinConfigPath())}; trap '' HUP; /usr/bin/env {env} {ShellQuote(fileName)} {arguments} > {ShellQuote(logPath)} 2>&1 < /dev/null & child=$!; " +
            installDns + $"/bin/sh -c {ShellQuote(watcher)} simple-watch \"$child\" > /dev/null 2>&1 < /dev/null & echo $child";
        if (MacPromptGate is not null && info.CoreType == ECoreType.sing_box)
            command = MacTunSupervisor.Launch(Environment.ProcessId, fileName, Utils.GetBinConfigPath(configPath),
                _macStopSignal, logPath, dnsAddress is null ? "true" : MacTunDns.Install(dnsOwner, dnsAddress) + " && (" + MacTunDns.Flush + ")",
                cleanupDns);
        string output;
        using var authorization = new CancellationTokenSource();
        _macAuthorization = authorization;
        try { output = await RunMacShell(command, authorization.Token); }
        catch (Exception ex)
        {
            File.Delete(_macStopSignal); _macStopSignal = null;
            throw new MacNetworkAuthorizationException("Network permission was not granted. Click Connect when you are ready.", ex);
        }
        finally { _macAuthorization = null; }
        if (!int.TryParse(output, out _linuxSudoPid) || _linuxSudoPid <= 0) throw new InvalidOperationException("TUN 启动未获得有效进程编号");
        await Task.Delay(700);
        var process = new ProcessService(_linuxSudoPid, _updateFunc);
        if (process.HasExited) throw new InvalidOperationException("TUN 启动失败，请查看 simple-tun.log");
        return process;
    }

}
