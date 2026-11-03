namespace WorldScience.Services;
/// <summary>Non-secret installation identity, independent of app version and sign-out.</summary>
public static class InstallationIdentity
{
    private static readonly object Gate = new();
    public static string Get()
    {
        lock (Gate)
        {
            var path = Path.Combine(Utils.StartupPath(), "installation-id");
            if (File.Exists(path))
            {
                var stored = File.ReadAllText(path).Trim();
                if (System.Text.RegularExpressions.Regex.IsMatch(stored, "^[a-f0-9]{32}$")) return stored;
                throw new InvalidOperationException("Unable to read this device identity. Please contact support.");
            }
            Directory.CreateDirectory(Utils.StartupPath());
            var id = Guid.NewGuid().ToString("N");
            // CreateNew prevents two startup instances from silently choosing different IDs.
            try { using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); using var writer = new StreamWriter(stream); writer.Write(id); }
            catch (IOException) when (File.Exists(path)) { return Get(); }
            return id;
        }
    }
    // The authenticated server supplies the canonical ID when migrating a legacy session.
    public static void Adopt(string id)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-f0-9]{32}$")) throw new InvalidOperationException("Invalid device identity response.");
        lock (Gate)
        {
            Directory.CreateDirectory(Utils.StartupPath());
            var path = Path.Combine(Utils.StartupPath(), "installation-id");
            if (File.Exists(path) && File.ReadAllText(path).Trim() == id) return;
            var temp = path + ".tmp"; File.WriteAllText(temp, id); File.Move(temp, path, true);
        }
    }
    public static string Platform => OperatingSystem.IsMacOS() ? "macOS" : "Windows";
}
