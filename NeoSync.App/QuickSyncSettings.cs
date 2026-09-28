namespace NeoSync.App;

/// <summary>Persists the user's chosen Quick Sync destination folder across runs.</summary>
internal static class QuickSyncSettings
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NeoSync", "quicksync-folder.txt");

    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NeoSync");

    public static string GetFolder()
    {
        try
        {
            string? saved = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath).Trim() : null;
            return string.IsNullOrEmpty(saved) ? DefaultFolder : saved;
        }
        catch
        {
            return DefaultFolder;
        }
    }

    public static void SetFolder(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, path);
    }
}
