// TEMP - remove after diagnosis
namespace NeoSync.Core.Usb;

internal static class DbgLog
{
    public static readonly string FilePath =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "neosync-debug.log");

    static DbgLog()
    {
        DiagnosticsSettings.AppendLog(FilePath, $"\r\n=== NeoSync session {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\r\n");
    }

    public static void W(string msg)
        => DiagnosticsSettings.AppendLog(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\r\n");
}
