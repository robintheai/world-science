using System.Text.RegularExpressions;
namespace WorldScience.Services;
internal static class MacVpnConflict
{
    internal static string? ConnectedService(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            if (!line.Contains("(Connected)", StringComparison.Ordinal)) continue;
            var match = Regex.Match(line, "\\\"([^\\\"]+)\\\"\\s+\\[VPN:");
            if (match.Success) return match.Groups[1].Value;
        }
        return null;
    }
    internal static string? PhysicalInterface(string text)
    {
        var match = Regex.Match(text, @"(?m)^\s*((?:en|bridge)\d+)\s*:\s*flags[^\n]*\(IPv4");
        return match.Success ? match.Groups[1].Value : null;
    }
    internal static async Task<string?> FindPhysicalInterface()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        using var process = Process.Start(new ProcessStartInfo("/usr/sbin/scutil") { ArgumentList = { "--nwi" }, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
        if (process is null) return null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0 ? PhysicalInterface(output) : null;
        }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(); return null; }
    }
    internal static async Task<string?> Find()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        using var process = Process.Start(new ProcessStartInfo("/usr/sbin/scutil") { ArgumentList = { "--nc", "list" }, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
        if (process is null) return null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0 ? ConnectedService(output) : null;
        }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(); return null; }
    }
}
