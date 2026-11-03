using System.Text.Json;
namespace WorldScience.Services;
public sealed record NodeMeasurement(double Speed, double? Delay, DateTimeOffset At);
public sealed class SimpleSettings
{
    public Dictionary<string, NodeMeasurement> NodeMeasurements { get; set; } = new();
    public string? ManualCountry { get; set; }
    public string? ManualNodeId { get; set; }
    public string? ManualNodeKey { get; set; }
    public DateTimeOffset? LastNodeSpeedCheck { get; set; }
    public DateTimeOffset? LastSubscriptionRefresh { get; set; }
    public string LatencyMode { get; set; } = "CONNECT";
    public string PingUrl { get; set; } = "https://cp.cloudflare.com/generate_204";
    public int PingTimeoutSeconds { get; set; } = 3;
    public string Mode { get; set; } = "全局代理";
    public bool Tun { get; set; } = true;
    public string Theme { get; set; } = "跟随系统";
    public bool ShowMenuNode { get; set; } = true;
    public bool ShowMenuSpeed { get; set; } = true;
    public DateTimeOffset? LastUpdateCheck { get; set; }
    /// <summary>Connect automatically when the app opens, using the last location choice.</summary>
    public bool AutoConnect { get; set; }
    /// <summary>When a chosen location stops working, use the fastest working one until it recovers.</summary>
    public bool Fallback { get; set; } = true;
    /// <summary>"auto" follows the system language; "zh" or "en" pins it.</summary>
    public string Language { get; set; } = "auto";
    public Dictionary<string, SubscriptionUsage> SubscriptionUsage { get; set; } = new();
    public DateTimeOffset? LastUsageWarning { get; set; }
    /// <summary>Subscriptions saved by an earlier edition were offered once.</summary>
    public bool LegacyImportDone { get; set; }
    private static string FilePath => Path.Combine(Utils.StartupPath(), "simple-settings.json");
    public static SimpleSettings Load()
    {
        try { return JsonSerializer.Deserialize<SimpleSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Utils.StartupPath());
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, FilePath, true);
    }
}
