using System.Net;
namespace WorldScience.Services;

public enum Availability { Checking, Available, Partial, Unavailable, Unknown }
public sealed record ServiceStatus(string Name, Availability State, string Detail)
{
    public bool IsAvailable => State == Availability.Available;
    public bool IsPartial => State == Availability.Partial;
    public bool IsUnavailable => State is Availability.Unavailable or Availability.Unknown;
    public string Symbol => State switch { Availability.Available => "✓", Availability.Partial => "◐", Availability.Checking => "…", _ => "✕" };
    public string Tip => $"{Name}: {Detail}";
}
public sealed record AvailabilityReport(string? Country, IReadOnlyList<ServiceStatus> Services, DateTimeOffset At);

/// <summary>
/// Answers the question novice users actually ask ("does ChatGPT work on this node?") by checking
/// the current exit region and each service through the running proxy. Small requests only.
/// </summary>
public static class ServiceAvailability
{
    // Regions where these services refuse access. Kept short and conservative; unknown regions are not flagged.
    private static readonly HashSet<string> AiBlocked = ["CN", "HK", "MO", "RU", "BY", "IR", "KP", "SY", "CU", "VE", "AF"];
    public static readonly string[] Names = ["ChatGPT", "Claude", "Gemini", "YouTube", "Netflix"];

    public static string? ParseTraceLocation(string? trace)
    {
        if (string.IsNullOrEmpty(trace)) return null;
        foreach (var line in trace.Split('\n'))
            if (line.StartsWith("loc=", StringComparison.Ordinal) && line.Trim().Length == 6) return line.Trim()[4..].ToUpperInvariant();
        return null;
    }
    public static Availability RegionVerdict(string? location) =>
        location is null ? Availability.Unknown : AiBlocked.Contains(location) ? Availability.Unavailable : Availability.Available;
    /// <summary>Netflix: the non-original title only opens in regions with a full catalogue.</summary>
    public static Availability NetflixVerdict(HttpStatusCode? licensed, HttpStatusCode? original) =>
        Opens(licensed) ? Availability.Available : Opens(original) ? Availability.Partial
        : licensed is null && original is null ? Availability.Unknown : Availability.Unavailable;
    private static bool Opens(HttpStatusCode? code) => code is HttpStatusCode.OK or HttpStatusCode.MovedPermanently or HttpStatusCode.Found;

    public static string Flag(string? country) => country is { Length: 2 } && country.All(char.IsAsciiLetterUpper)
        ? string.Concat(country.Select(c => char.ConvertFromUtf32(0x1F1E6 + c - 'A'))) : "🌐";

    public static async Task<AvailabilityReport> Check(HttpClient proxied, CancellationToken token)
    {
        async Task<string?> Text(string url)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(8));
                return await proxied.GetStringAsync(url, timeout.Token);
            }
            catch (Exception) when (!token.IsCancellationRequested) { return null; }
        }
        async Task<HttpStatusCode?> Status(string url)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(8));
                using var response = await proxied.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                return response.StatusCode;
            }
            catch (Exception) when (!token.IsCancellationRequested) { return null; }
        }
        var exit = Text("https://1.1.1.1/cdn-cgi/trace");
        var chatgpt = Text("https://chatgpt.com/cdn-cgi/trace");
        var claude = Text("https://claude.ai/cdn-cgi/trace");
        var gemini = Status("https://gemini.google.com/");
        var youtube = Status("https://www.youtube.com/generate_204");
        var licensed = Status("https://www.netflix.com/title/70143836");
        var original = Status("https://www.netflix.com/title/81280792");
        await Task.WhenAll(exit, chatgpt, claude, gemini, youtube, licensed, original);
        var country = ParseTraceLocation(await exit);

        ServiceStatus Ai(string name, string? trace)
        {
            var location = ParseTraceLocation(trace);
            if (location is null) return new(name, Availability.Unavailable, Ui.T("Not reachable", "无法访问"));
            var verdict = RegionVerdict(location);
            return new(name, verdict, verdict == Availability.Available ? Ui.T("Available", "可用") : Ui.T($"Not offered in {location}", $"{location} 地区不可用"));
        }
        var geminiState = await gemini is null ? Availability.Unavailable : RegionVerdict(country) == Availability.Unavailable ? Availability.Unavailable : Availability.Available;
        var youtubeState = await youtube == HttpStatusCode.NoContent ? Availability.Available : Availability.Unavailable;
        var netflix = NetflixVerdict(await licensed, await original);
        return new AvailabilityReport(country,
        [
            Ai("ChatGPT", await chatgpt), Ai("Claude", await claude),
            new("Gemini", geminiState, geminiState == Availability.Available ? Ui.T("Available", "可用") : Ui.T("Unavailable", "不可用")),
            new("YouTube", youtubeState, youtubeState == Availability.Available ? Ui.T("Available", "可用") : Ui.T("Not reachable", "无法访问")),
            new("Netflix", netflix, netflix switch
            {
                Availability.Available => Ui.T("Full catalogue", "完整片库"),
                Availability.Partial => Ui.T("Originals only", "仅自制剧"),
                Availability.Unknown => Ui.T("Not reachable", "无法访问"),
                _ => Ui.T("Unavailable", "不可用")
            })
        ], DateTimeOffset.UtcNow);
    }
}
