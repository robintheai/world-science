using System.Globalization;
namespace WorldScience.Services;

internal static class CompactMenuTraffic
{
    internal static string Rate(long bytes)
    {
        bytes = Math.Max(0, bytes);
        if (bytes >= 1_000_000_000_000) return ">999G/s";
        if (bytes >= 1_000_000_000) return (bytes / 1_000_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "G/s";
        if (bytes >= 1_000_000) return (bytes / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M/s";
        if (bytes >= 1000) return (bytes / 1000d).ToString("0.#", CultureInfo.InvariantCulture) + "K/s";
        return bytes.ToString(CultureInfo.InvariantCulture) + "B/s";
    }
    internal static string Title(bool connected, long up, long down) => connected
        ? $"{Rate(up)}↑\n{Rate(down)}↓" : "—↑\n—↓";
}
