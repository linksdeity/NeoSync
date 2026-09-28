// Removes WinUSB OEM driver packages for the Neo's two USB personalities (BD04/BD01).
// Installation is handled elsewhere via pnputil + the signed driver package
// (see Bd04WinUsbCycle.cs and NeoSyncWorker.CmdSetupDrivers) -- this class is
// now removal-only.
using System.Diagnostics;
using System.Runtime.Versioning;

namespace NeoSync.Core.Usb;

public static class NeoDriverInstaller
{
    /// <summary>
    /// Removes the WinUSB OEM driver for one device by its published INF name (e.g. oem6.inf).
    /// Runs pnputil /delete-driver which requires elevation; returns true on success.
    /// </summary>
    public static (bool ok, string output) RemoveWinUsb(string infPath)
    {
        if (string.IsNullOrWhiteSpace(infPath))
            return (false, "No INF path provided.");
        return RunProcess("pnputil.exe", $"/delete-driver \"{infPath}\" /uninstall /force");
    }

    /// <summary>Remove WinUSB from both Neo devices using INF paths from NeoDriverState.</summary>
    /// <remarks>
    /// Scans C:\Windows\INF for every OEM INF referencing each VID/PID and deletes them
    /// all in one pass, preventing Windows from silently reassigning a fallback package.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public static (bool ok04, string out04, bool ok01, string out01) RemoveBoth(
        string? hidInfPath, string? commsInfPath)
    {
        var (ok04, out04) = RemoveAllMatchingInfs("VID_081E&PID_BD04");
        var (ok01, out01) = RemoveAllMatchingInfs("VID_081E&PID_BD01");
        return (ok04, out04, ok01, out01);
    }

    [SupportedOSPlatform("windows")]
    private static (bool ok, string output) RemoveAllMatchingInfs(string vidPid)
    {
        string infDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");

        var matches = new List<string>();
        foreach (string path in Directory.EnumerateFiles(infDir, "oem*.inf"))
        {
            try
            {
                if (File.ReadAllText(path).Contains(vidPid, StringComparison.OrdinalIgnoreCase))
                    matches.Add(Path.GetFileName(path));
            }
            catch { }
        }

        if (matches.Count == 0)
            return (false, $"No staged INFs found for {vidPid}.");

        var sb = new System.Text.StringBuilder();
        bool anyOk = false;
        foreach (string inf in matches)
        {
            var (ok, output) = RemoveWinUsb(inf);
            sb.AppendLine($"{inf}: {(ok ? "removed" : "failed")} - {output.Trim()}");
            if (ok) anyOk = true;
        }
        return (anyOk, sb.ToString().Trim());
    }

    private static (bool ok, string output) RunProcess(string fileName, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            using var proc = Process.Start(psi)!;
            string output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return (proc.ExitCode == 0, output.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
