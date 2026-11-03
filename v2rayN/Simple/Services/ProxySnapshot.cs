using System.Text.Json;
using Microsoft.Win32;
namespace WorldScience.Services;
public sealed class ProxySnapshot
{
    public record MacService(string Name, string Kind, bool Enabled, string Server, int Port);
    public List<MacService> Mac { get; set; } = [];
    public Dictionary<string, string?> Windows { get; set; } = [];
    public static async Task<ProxySnapshot> Capture()
    {
        var result = new ProxySnapshot();
        if (OperatingSystem.IsMacOS())
        {
            var services = (await Run("/usr/sbin/networksetup", "-listallnetworkservices")).Split('\n').Skip(1)
                .Select(s => s.Trim()).Where(s => s.Length > 0 && !s.StartsWith('*'));
            foreach (var service in services)
            foreach (var kind in new[] { "web", "secureweb", "socksfirewall" })
            {
                var values = (await Run("/usr/sbin/networksetup", $"-get{kind}proxy", service)).Split('\n')
                    .Select(l => l.Split(": ", 2)).Where(a => a.Length == 2).ToDictionary(a => a[0], a => a[1].Trim());
                if (!values.ContainsKey("Enabled")) continue;
                if (values.GetValueOrDefault("Authenticated Proxy Enabled") == "1")
                    throw new InvalidOperationException("当前系统代理需要身份认证；请先在系统设置中停用，再连接 Simple，以便保留原有认证设置。");
                result.Mac.Add(new(service, kind, values["Enabled"] == "Yes", values.GetValueOrDefault("Server", ""), int.TryParse(values.GetValueOrDefault("Port"), out var port) ? port : 0));
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            foreach (var name in new[] { "ProxyEnable", "ProxyServer", "ProxyOverride", "AutoConfigURL" })
                result.Windows[name] = key?.GetValue(name)?.ToString();
        }
        return result;
    }
    public async Task Apply(int port)
    {
        foreach (var service in Mac)
        {
            await Run("/usr/sbin/networksetup", $"-set{service.Kind}proxy", service.Name, "127.0.0.1", port.ToString());
            await Run("/usr/sbin/networksetup", $"-set{service.Kind}proxystate", service.Name, "on");
        }
    }
    public async Task Restore()
    {
        if (OperatingSystem.IsMacOS())
        {
            foreach (var service in Mac)
            {
                if (service.Port > 0 && service.Server.Length > 0)
                    await Run("/usr/sbin/networksetup", $"-set{service.Kind}proxy", service.Name, service.Server, service.Port.ToString());
                await Run("/usr/sbin/networksetup", $"-set{service.Kind}proxystate", service.Name, service.Enabled ? "on" : "off");
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true);
            foreach (var (name, value) in Windows)
            {
                if (value is null) key?.DeleteValue(name, false);
                else if (name == "ProxyEnable") key?.SetValue(name, int.Parse(value), RegistryValueKind.DWord);
                else key?.SetValue(name, value, RegistryValueKind.String);
            }
            InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
        }
    }
    public static async Task<string> Run(string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动系统工具");
        var outputTask = process.StandardOutput.ReadToEndAsync(); var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask; var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException("系统代理设置失败：" + error);
        return output;
    }
    [DllImport("wininet.dll")] private static extern bool InternetSetOption(IntPtr h, int option, IntPtr buffer, int length);
}
