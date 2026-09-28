// TEMP - remove after diagnosis
namespace NeoSync.Core.Usb;

internal static class DbgLog
{
    public static readonly string FilePath =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "neosync-debug.log");

    static DbgLog()
    {
        if (!DiagnosticsSettings.IsEnabled) return;
        try
        {
            System.IO.File.AppendAllText(FilePath,
                $"\r\n=== NeoSync session {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\r\n");
        }
        catch { }
    }

    public static void W(string msg)
    {
        if (!DiagnosticsSettings.IsEnabled) return;
        try { System.IO.File.AppendAllText(FilePath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\r\n"); }
        catch { }
    }
}
