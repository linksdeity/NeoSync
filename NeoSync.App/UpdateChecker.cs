using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace NeoSync.App;

/// <summary>Checks GitHub Releases for a newer NeoSync version.</summary>
internal static class UpdateChecker
{
    // TODO: point this at the real "owner/repo" once NeoSync is pushed to a public GitHub repo.
    private const string GitHubRepo = "neosync-project/neosync";

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

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

        if (tag is null || !Version.TryParse(tag.TrimStart('v', 'V'), out Version? latest))
        {
            MessageBox.Show(owner, "Couldn't determine the latest version.",
                "NeoSync - Check for Updates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

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
