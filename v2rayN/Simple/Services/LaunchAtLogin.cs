using System.Security;
namespace WorldScience.Services;

/// <summary>
/// Opens Owlpath when the user signs in. macOS uses a per-user LaunchAgent. Windows uses a scheduled
/// task with highest privileges because the app manifest requires elevation, which a Run key cannot provide.
/// </summary>
internal static class LaunchAtLogin
{
    private const string Label = "io.github.robintheai.worldscience.login";
    private const string TaskName = "Owlpath";
    private const string LegacyTaskName = "World Science";
    private static string AgentPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", Label + ".plist");

    /// <summary>The .app bundle when running from one, so macOS shows the normal app identity.</summary>
    internal static string? MacBundle(string baseDirectory)
    {
        var marker = ".app/Contents/MacOS";
        var index = baseDirectory.IndexOf(marker, StringComparison.Ordinal);
        return index < 0 ? null : baseDirectory[..(index + 4)];
    }
    internal static string AgentPlist(string bundle) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
        $"<plist version=\"1.0\"><dict><key>Label</key><string>{Label}</string><key>ProgramArguments</key><array><string>/usr/bin/open</string><string>-g</string><string>{SecurityElement.Escape(bundle)}</string><string>--args</string><string>--background</string></array>" +
        "<key>RunAtLoad</key><true/><key>ProcessType</key><string>Interactive</string></dict></plist>\n";

    internal static bool Supported => OperatingSystem.IsMacOS() ? MacBundle(AppContext.BaseDirectory) is not null : OperatingSystem.IsWindows();

    internal static bool IsEnabled()
    {
        if (OperatingSystem.IsMacOS()) return File.Exists(AgentPath);
        if (!OperatingSystem.IsWindows()) return false;
        try { return Run("schtasks.exe", "/Query", "/TN", TaskName).ExitCode == 0; } catch { return false; }
    }

    internal static void Set(bool enabled)
    {
        if (OperatingSystem.IsMacOS())
        {
            if (!enabled) { if (File.Exists(AgentPath)) File.Delete(AgentPath); return; }
            var bundle = MacBundle(AppContext.BaseDirectory) ?? throw new InvalidOperationException(Ui.T("Move Owlpath to Applications first.", "请先把 Owlpath 拖到「应用程序」文件夹。"));
            Directory.CreateDirectory(Path.GetDirectoryName(AgentPath)!);
            var tmp = AgentPath + ".tmp"; File.WriteAllText(tmp, AgentPlist(bundle)); File.Move(tmp, AgentPath, true);
            return;
        }
        if (!OperatingSystem.IsWindows()) return;
        var exe = Path.Combine(AppContext.BaseDirectory, "WorldScience.exe");
        (int ExitCode, string Output) result;
        if (enabled)
        {
            // Plain /Create inherits Task Scheduler defaults that skip the start on battery, kill the app when the
            // laptop is unplugged and stop it after 72 hours, which could leave the system proxy or TUN behind.
            var xml = Path.Combine(Path.GetTempPath(), $"world-science-task-{Guid.NewGuid():N}.xml");
            try
            {
                File.WriteAllText(xml, TaskXml(exe, Environment.UserDomainName + "\\" + Environment.UserName), new System.Text.UnicodeEncoding(false, true));
                result = Run("schtasks.exe", "/Create", "/F", "/TN", TaskName, "/XML", xml);
            }
            finally { try { File.Delete(xml); } catch (IOException) { } }
        }
        else result = Run("schtasks.exe", "/Delete", "/F", "/TN", TaskName);
        if (result.ExitCode != 0 && (enabled || IsEnabled()))
            throw new InvalidOperationException(Ui.T("Windows did not accept the startup setting.", "Windows 没有接受开机启动设置，请以管理员身份运行后再试。"));
    }

    /// <summary>Points an enabled macOS login item at this app, for example after the bundle was renamed.</summary>
    internal static void Refresh()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // The task was named after the app's earlier name; replace it so the switch reflects it.
                if (Run("schtasks.exe", "/Query", "/TN", LegacyTaskName).ExitCode == 0)
                { Set(true); Run("schtasks.exe", "/Delete", "/F", "/TN", LegacyTaskName); }
                return;
            }
            if (!OperatingSystem.IsMacOS() || !File.Exists(AgentPath) || MacBundle(AppContext.BaseDirectory) is not { } bundle) return;
            if (File.ReadAllText(AgentPath) != AgentPlist(bundle)) Set(true);
        }
        catch (Exception) { /* Best effort: the user can still turn it on in Settings. */ }
    }
    /// <summary>An earlier edition's login item would start the old app next to this one. Replace it with ours.
    /// Their LaunchAgents used the com.robintheai prefix; their Windows tasks started v2rayNSimple.exe.</summary>
    internal static void MigrateLegacy()
    {
        try
        {
            var found = false;
            if (OperatingSystem.IsMacOS())
            {
                foreach (var legacy in Directory.EnumerateFiles(Path.GetDirectoryName(AgentPath)!, "com.robintheai.*.login.plist"))
                { File.Delete(legacy); found = true; }
            }
            else if (OperatingSystem.IsWindows())
            {
                found = Run("powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
                    "$t = Get-ScheduledTask | Where-Object { $_.Actions.Execute -like '*\\v2rayNSimple.exe' }; if ($t) { $t | Unregister-ScheduledTask -Confirm:$false; exit 0 } else { exit 3 }").ExitCode == 0;
            }
            if (found && Supported && !IsEnabled()) Set(true);
        }
        catch (Exception) { /* Best effort: the user can still turn it on in Settings. */ }
    }

    internal static string TaskXml(string exe, string user) =>
        "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
        $"<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(user)}</UserId><Delay>PT15S</Delay></LogonTrigger></Triggers>" +
        $"<Principals><Principal id=\"Author\"><UserId>{SecurityElement.Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
        "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
        "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit><AllowHardTerminate>false</AllowHardTerminate><StartWhenAvailable>true</StartWhenAvailable><Enabled>true</Enabled></Settings>" +
        $"<Actions Context=\"Author\"><Exec><Command>{SecurityElement.Escape(exe)}</Command><Arguments>--background</Arguments></Exec></Actions></Task>";
    private static (int ExitCode, string Output) Run(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("schtasks");
        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(10_000)) { process.Kill(); return (-1, output); }
        return (process.ExitCode, output);
    }
}
