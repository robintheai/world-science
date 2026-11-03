using System.Diagnostics;
using System.Globalization;
namespace WorldScience.Services;

/// <summary>Interface language follows the system: Chinese systems get Chinese text, everything else English.</summary>
public static class Ui
{
    private static bool? _chinese;
    // Captured before the engine pins the thread culture to en-US for upstream parsing.
    public static bool Chinese => _chinese ??= Detect(SystemCulture(), Environment.GetEnvironmentVariable("WORLD_SCIENCE_LANGUAGE"));
    public static void Use(string? language) => _chinese = Detect(SystemCulture(), language);
    // Finder-launched apps have no LANG, so .NET reports en-US there. Ask macOS for the preferred language.
    private static string SystemCulture()
    {
        if (!OperatingSystem.IsMacOS()) return CultureInfo.CurrentUICulture.Name;
        try
        {
            using var process = Process.Start(new ProcessStartInfo("/usr/bin/defaults") { ArgumentList = { "read", "-g", "AppleLanguages" }, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (process is null) return CultureInfo.CurrentUICulture.Name;
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(2000)) { process.Kill(); return CultureInfo.CurrentUICulture.Name; }
            return FirstAppleLanguage(output) ?? CultureInfo.CurrentUICulture.Name;
        }
        catch { return CultureInfo.CurrentUICulture.Name; }
    }
    internal static string? FirstAppleLanguage(string output) =>
        output.Split(['\n', ',', '(', ')'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().Trim('"')).FirstOrDefault(s => s.Length >= 2);
    internal static bool Detect(string culture, string? overrideLanguage) => overrideLanguage?.Trim().ToLowerInvariant() switch
    {
        "zh" or "zh-cn" or "chinese" => true,
        "en" or "english" => false,
        _ => culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
    };
    public static string T(string english, string chinese) => Chinese ? chinese : english;
    public static string Duration(TimeSpan span) => span.TotalHours >= 1
        ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{span.Minutes:00}:{span.Seconds:00}";
    public static string Bytes(long value) => value >= 1L << 40 ? $"{value / (double)(1L << 40):0.##} TB"
        : value >= 1L << 30 ? $"{value / (double)(1L << 30):0.##} GB" : value >= 1L << 20 ? $"{value / (double)(1L << 20):0.#} MB" : $"{Math.Max(0, value) / 1024d:0} KB";
    private static readonly Dictionary<string, string> Countries = new()
    {
        ["United States"] = "美国", ["Japan"] = "日本", ["Germany"] = "德国", ["United Kingdom"] = "英国", ["Netherlands"] = "荷兰",
        ["Singapore"] = "新加坡", ["Hong Kong"] = "香港", ["Taiwan"] = "台湾", ["South Korea"] = "韩国", ["Canada"] = "加拿大",
        ["Australia"] = "澳大利亚", ["New Zealand"] = "新西兰", ["France"] = "法国", ["Switzerland"] = "瑞士", ["Sweden"] = "瑞典",
        ["Finland"] = "芬兰", ["India"] = "印度", ["Brazil"] = "巴西", ["Italy"] = "意大利", ["Spain"] = "西班牙",
        ["Thailand"] = "泰国", ["Malaysia"] = "马来西亚", ["Other locations"] = "其他地区"
    };
    public static string Country(string english) => Chinese && Countries.TryGetValue(english, out var name) ? name : english;
}
