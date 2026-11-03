using SQLite;
namespace WorldScience.Services;

/// <summary>
/// World Science replaces earlier editions of this client, which kept their subscriptions in their own data folders.
/// On the first launch with no subscriptions, their saved links are offered to the normal importer.
/// Only the links are read; their nodes and credentials are downloaded fresh and validated like a pasted link.
/// </summary>
internal static class LegacyImport
{
    /// <summary>Earlier editions keep simple-settings.json next to a v2rayN database; upstream v2rayN has no such file.
    /// Most recently used first.</summary>
    internal static List<string> Folders(string root)
    {
        if (!Directory.Exists(root)) return [];
        var own = Utils.StartupPath().TrimEnd('/', '\\');
        return Directory.EnumerateDirectories(root)
            .Where(d => Path.GetFileName(d) != "World-Science" && d.TrimEnd('/', '\\') != own
                && File.Exists(Path.Combine(d, "simple-settings.json")) && File.Exists(Path.Combine(d, "guiConfigs", "guiNDB.db")))
            .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "guiConfigs", "guiNDB.db"))).ToList();
    }
    /// <summary>The folder holding the old apps' data. Tests point it at a fixture; never at the user's real data.</summary>
    internal static string Root => Environment.GetEnvironmentVariable("WORLD_SCIENCE_LEGACY_ROOT") is { Length: > 0 } root
        ? root : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>Enabled http(s) subscription links without duplicates. Unreadable databases are skipped.</summary>
    internal static List<Uri> Read(string root)
    {
        var links = new List<Uri>();
        foreach (var folder in Folders(root))
        {
            var path = Path.Combine(folder, "guiConfigs", "guiNDB.db");
            try
            {
                using var db = new SQLiteConnection(new SQLiteConnectionString(path, SQLiteOpenFlags.ReadOnly, false));
                foreach (var sub in db.Query<SubItem>("SELECT * FROM SubItem WHERE Enabled = 1 ORDER BY Sort"))
                {
                    Uri uri;
                    try { uri = LinkSubscriptions.ParseUrl(sub.Url ?? ""); } catch (InvalidOperationException) { continue; }
                    if (links.All(l => l.AbsoluteUri != uri.AbsoluteUri)) links.Add(uri);
                }
            }
            catch (Exception ex) when (ex is SQLiteException or IOException or UnauthorizedAccessException) { Logging.SaveLog("Simple.LegacyImport", ex); }
        }
        return links;
    }

    /// <summary>"Connect automatically" chosen in an earlier edition, if it was on.</summary>
    internal static bool AutoConnect(string root) => Folders(root).Any(folder =>
    {
        try { return System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "simple-settings.json"))).RootElement.TryGetProperty("AutoConnect", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.True; }
        catch { return false; }
    });
}
