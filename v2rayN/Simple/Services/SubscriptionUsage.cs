namespace WorldScience.Services;

/// <summary>Traffic and expiry from the de-facto <c>subscription-userinfo</c> response header.</summary>
public sealed record SubscriptionUsage(long Upload, long Download, long Total, DateTimeOffset? Expire, DateTimeOffset At)
{
    public long Used => Math.Max(0, Upload) + Math.Max(0, Download);
    public long? Remaining => Total > 0 ? Math.Max(0, Total - Used) : null;
    public double? UsedFraction => Total > 0 ? Math.Clamp(Used / (double)Total, 0, 1) : null;
    public int? DaysLeft(DateTimeOffset now) => Expire is { } at ? (int)Math.Floor((at - now).TotalDays) : null;
    public bool Expired(DateTimeOffset now) => Expire is { } at && at <= now;
    /// <summary>Running out of data (≤10 % left) or expiring within three days.</summary>
    public bool NeedsAttention(DateTimeOffset now) => Expired(now) || DaysLeft(now) is < 3 || (UsedFraction is { } used && used >= 0.9);

    public static SubscriptionUsage? Parse(string? header, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        long upload = 0, download = 0, total = 0; DateTimeOffset? expire = null; var any = false;
        foreach (var part in header.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || !double.TryParse(pair[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)) continue;
            var number = (long)Math.Clamp(value, 0, long.MaxValue);
            switch (pair[0].ToLowerInvariant())
            {
                case "upload": upload = number; any = true; break;
                case "download": download = number; any = true; break;
                case "total": total = number; any = true; break;
                // 0 means "never expires" for most panels.
                case "expire" when number > 0 && number < 32503680000: expire = DateTimeOffset.FromUnixTimeSeconds(number); any = true; break;
            }
        }
        return any ? new SubscriptionUsage(upload, download, total, expire, now) : null;
    }

    public string Summary(DateTimeOffset now)
    {
        var parts = new List<string>();
        if (Remaining is { } left) parts.Add(Ui.T($"{Ui.Bytes(left)} left of {Ui.Bytes(Total)}", $"剩余 {Ui.Bytes(left)} / 共 {Ui.Bytes(Total)}"));
        else if (Used > 0) parts.Add(Ui.T($"{Ui.Bytes(Used)} used", $"已用 {Ui.Bytes(Used)}"));
        if (Expire is { } at)
            parts.Add(Expired(now) ? Ui.T("expired", "已到期") : DaysLeft(now) is 0 ? Ui.T("expires today", "今天到期")
                : Ui.T($"expires {at.ToLocalTime():yyyy-MM-dd}", $"{at.ToLocalTime():yyyy-MM-dd} 到期"));
        return string.Join(" · ", parts);
    }

    public string? Warning(DateTimeOffset now) =>
        Expired(now) ? Ui.T("Your subscription has expired. Renew it with your provider.", "订阅已到期，请联系服务商续费。")
        : DaysLeft(now) is { } days and < 3 ? Ui.T($"Your subscription expires in {Math.Max(days, 0)} day(s).", days <= 0 ? "订阅今天到期，记得续费。" : $"订阅还有 {days} 天到期，记得续费。")
        : UsedFraction is { } used && used >= 0.9 ? Ui.T("Less than 10% of your data is left.", "订阅流量已用超过 90%，注意余量。")
        : null;
}
