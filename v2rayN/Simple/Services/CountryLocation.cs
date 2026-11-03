namespace WorldScience.Services;

public static class CountryLocation
{
    private static readonly (string Name, string Flag, string[] Aliases)[] Countries =
    [
        ("United States", "🇺🇸", ["美国", "United States", "US-VPS", "USA"]),
        ("Japan", "🇯🇵", ["日本", "Japan"]), ("Germany", "🇩🇪", ["德国", "Germany"]),
        ("United Kingdom", "🇬🇧", ["英国", "United Kingdom", "Britain"]),
        ("Netherlands", "🇳🇱", ["荷兰", "Netherlands"]), ("Singapore", "🇸🇬", ["新加坡", "Singapore"]),
        ("Hong Kong", "🇭🇰", ["香港", "Hong Kong"]), ("Taiwan", "🇹🇼", ["台湾", "Taiwan"]),
        ("South Korea", "🇰🇷", ["韩国", "South Korea", "Korea"]), ("Canada", "🇨🇦", ["加拿大", "Canada"]),
        ("Australia", "🇦🇺", ["澳大利亚", "Australia"]), ("New Zealand", "🇳🇿", ["新西兰", "New Zealand"]),
        ("France", "🇫🇷", ["法国", "France"]), ("Switzerland", "🇨🇭", ["瑞士", "Switzerland"]),
        ("Sweden", "🇸🇪", ["瑞典", "Sweden"]), ("Finland", "🇫🇮", ["芬兰", "Finland"]),
        ("India", "🇮🇳", ["印度", "India"]), ("Brazil", "🇧🇷", ["巴西", "Brazil"]),
        ("Italy", "🇮🇹", ["意大利", "Italy"]), ("Spain", "🇪🇸", ["西班牙", "Spain"]),
        ("Thailand", "🇹🇭", ["泰国", "Thailand"]), ("Malaysia", "🇲🇾", ["马来西亚", "Malaysia"])
    ];
    public static string Name(string node)
    {
        foreach (var country in Countries) if (node.Contains(country.Flag)) return country.Name;
        foreach (var country in Countries)
            if (country.Aliases.Any(a => node.Contains(a, StringComparison.OrdinalIgnoreCase))) return country.Name;
        return "Other locations";
    }
    public static string Flag(string country) => Countries.FirstOrDefault(c => c.Name == country).Flag ?? "◎";
}
