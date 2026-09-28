// Shared on/off switch for NeoSync's diagnostic logging, honored by both the app and
// the service. Logs never contain file content -- only command codes, timing, and
// Windows driver/registry state -- but writing them at all should still be consensual.
namespace NeoSync.Core.Usb;

public static class DiagnosticsSettings
{
    private static readonly string FlagPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "NeoSync", "diagnostics.flag");

    /// <summary>Defaults to enabled if no flag file exists yet (e.g. running outside the installer).</summary>
    public static bool IsEnabled
    {
        get
        {
            try { return !File.Exists(FlagPath) || File.ReadAllText(FlagPath).Trim() != "0"; }
            catch { return true; }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FlagPath)!);
            File.WriteAllText(FlagPath, enabled ? "1" : "0");
        }
        catch { }
    }

    /// <summary>Appends to a log file only if diagnostics are currently enabled.</summary>
    public static void AppendLog(string path, string text)
    {
        if (!IsEnabled) return;
        try { File.AppendAllText(path, text); } catch { }
    }
}
