namespace NeoSync.App;

/// <summary>Persists the user's chosen visual theme across runs.</summary>
internal static class ThemeSettings
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NeoSync", "theme.txt");

    public static AppTheme GetTheme()
    {
        try
        {
            string? saved = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath).Trim() : null;
            return AppTheme.ByName(saved);
        }
        catch
        {
            return AppTheme.Default;
        }
    }

    public static void SetTheme(AppTheme theme)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, theme.Name);
    }
}
