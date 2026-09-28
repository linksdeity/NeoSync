using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace NeoSync.App;

/// <summary>Checks GitHub Releases for a newer NeoSync version.</summary>
internal static class UpdateChecker
{
    private const string GitHubRepo = "linksdeity/NeoSync";

    // AssemblyVersion is always 4-part (trailing .0 for Revision) -- drop it for a clean
    // semver-style display, and so comparisons against a 3-part GitHub tag aren't skewed
    // by Revision defaulting to -1 (undefined) on one side and 0 on the other.
    public static Version CurrentVersion
    {
        get
        {
            Version v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, v.Build);
        }
    }

    public static async Task CheckAsync(Form owner)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NeoSync-UpdateCheck");

        string json;
        try
        {
            json = await http.GetStringAsync($"https://api.github.com/repos/{GitHubRepo}/releases/latest");
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"Couldn't check for updates:\r\n{ex.Message}",
                "NeoSync - Check for Updates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string? tag, htmlUrl;
        try
        {
            using var doc = JsonDocument.Parse(json);
            tag     = doc.RootElement.GetProperty("tag_name").GetString();
            htmlUrl = doc.RootElement.GetProperty("html_url").GetString();
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"Couldn't read the latest release info:\r\n{ex.Message}",
                "NeoSync - Check for Updates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (tag is null || !Version.TryParse(tag.TrimStart('v', 'V'), out Version? latestRaw))
        {
            MessageBox.Show(owner, "Couldn't determine the latest version.",
                "NeoSync - Check for Updates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        // Normalize to 3 parts too, matching CurrentVersion (Build defaults to -1 if the tag
        // only has 2 parts, e.g. "v0.9" -- clamp so the comparison below is always valid).
        var latest = new Version(latestRaw.Major, latestRaw.Minor, Math.Max(latestRaw.Build, 0));

        if (latest <= CurrentVersion)
        {
            MessageBox.Show(owner, $"You're up to date (v{CurrentVersion}).",
                "NeoSync - Check for Updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var result = MessageBox.Show(owner,
            $"A new version is available: v{latest} (you have v{CurrentVersion}).\r\n\r\nOpen the download page?",
            "NeoSync - Update Available", MessageBoxButtons.YesNo, MessageBoxIcon.Information);

        if (result == DialogResult.Yes && htmlUrl is not null)
        {
            try { Process.Start(new ProcessStartInfo(htmlUrl) { UseShellExecute = true }); }
            catch { }
        }
    }
}
