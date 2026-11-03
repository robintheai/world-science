using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
namespace WorldScience.Services;

/// <summary>
/// A support bundle the user can send without exposing what they browsed: engine traffic lines
/// (which name visited sites) are dropped, and URLs or IP addresses in the remaining lines are masked.
/// </summary>
internal static class Diagnostics
{
    private static readonly Regex Url = new(@"(?i)\b(?:https?|vless|vmess|trojan|ss|hy2|hysteria2|tuic|anytls)://\S+", RegexOptions.Compiled);
    private static readonly Regex Ipv4 = new(@"\b(?!127\.0\.0\.1\b)(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.Compiled);
    private static readonly Regex Email = new(@"[\w.+-]+@[\w-]+\.[\w.-]+", RegexOptions.Compiled);

    internal static string Redact(string line) => Email.Replace(Ipv4.Replace(Url.Replace(line, "<link>"), "<ip>"), "<email>");
    /// <summary>App-level lines only; core lines carry destination domains of the user's own traffic.</summary>
    internal static bool Keep(string line) => !line.Contains("Simple.Core:", StringComparison.Ordinal);

    internal static string Export(string dataDirectory, string version)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop)) desktop = Path.GetTempPath();
        var target = Path.Combine(desktop, $"World-Science-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip");
        using var archive = ZipFile.Open(target, ZipArchiveMode.Create);
        var summary = new StringBuilder()
            .AppendLine($"World Science {version}")
            .AppendLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})")
            .AppendLine($"Exported: {DateTimeOffset.Now:O}")
            .AppendLine($"Network up: {NetworkState.PhysicalNetworkUp()}");
        WriteEntry(archive, "summary.txt", summary.ToString());
        var logs = Path.Combine(dataDirectory, "guiLogs");
        if (Directory.Exists(logs))
            foreach (var file in new DirectoryInfo(logs).GetFiles("*.txt").OrderByDescending(f => f.LastWriteTimeUtc).Take(3))
            {
                var lines = ReadShared(file.FullName).Split('\n').Where(Keep).Select(Redact);
                WriteEntry(archive, "logs/" + file.Name, string.Join('\n', lines.TakeLast(20_000)));
            }
        return target;
    }
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    private static void WriteEntry(ZipArchive archive, string name, string text)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }
}
