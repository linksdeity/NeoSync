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
        AppendLogAlways(path, text);
    }

    /// <summary>
    /// Appends regardless of the enable/disable toggle -- for explicit one-off actions
    /// like Copy Diagnostics, where clicking the button is itself the consent. Still capped.
    /// </summary>
    public static void AppendLogAlways(string path, string text)
    {
        try
        {
            TrimIfTooBig(path);
            File.AppendAllText(path, text);
        }
        catch { }
    }

    // These are diagnostic logs meant to be read by a person, not an audit trail -- cap
    // size rather than letting them grow unbounded over months/years of use.
    private const long MaxLogBytes = 2 * 1024 * 1024; // 2 MB

    private static void TrimIfTooBig(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= MaxLogBytes) return;

            // Keep roughly the newest half rather than wiping it entirely, so there's
            // still some recent history to look at right after a trim.
            string content = File.ReadAllText(path);
            int keepFrom = content.Length - (int)(MaxLogBytes / 2);
            if (keepFrom > 0)
            {
                int nl = content.IndexOf('\n', keepFrom); // avoid starting mid-line
                content = content[(nl >= 0 ? nl + 1 : keepFrom)..];
            }
            File.WriteAllText(path,
                $"=== log trimmed {DateTime.Now:yyyy-MM-dd HH:mm:ss} (exceeded {MaxLogBytes / 1024 / 1024} MB) ===\r\n" + content);
        }
        catch { }
    }
}
