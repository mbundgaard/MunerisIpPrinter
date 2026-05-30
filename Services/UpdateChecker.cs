using System.Net.Http;
using System.Text.Json;

namespace MunerisIpPrinter.Services;

public sealed record UpdateInfo(Version LatestVersion, string ReleaseUrl);

/// <summary>
/// Asks GitHub's Releases API whether there's a newer published release than the running build.
/// Quiet on every failure mode (offline, rate-limited, unparseable tag) — update checks must
/// never be the reason the app misbehaves.
/// </summary>
public static class UpdateChecker
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(8),
    };

    static UpdateChecker()
    {
        // GitHub rejects requests with no User-Agent.
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("MunerisIpPrinter-Updater");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>Returns the latest release if it's strictly newer than <paramref name="current"/>, otherwise null.</summary>
    /// <param name="repo">"owner/repo" — e.g. "muneris/MunerisIpPrinter".</param>
    public static async Task<UpdateInfo?> CheckAsync(string repo, Version current, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://api.github.com/repos/{repo}/releases/latest";
            var json = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            var root = doc.RootElement;
            if (!root.TryGetProperty("tag_name", out var tagEl)) return null;
            if (!root.TryGetProperty("html_url", out var urlEl)) return null;

            var tag = tagEl.GetString();
            var releaseUrl = urlEl.GetString();
            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(releaseUrl)) return null;

            // Tags are conventionally "v1.2.3"; strip the leading v.
            var versionPart = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
            if (!Version.TryParse(versionPart, out var latest)) return null;

            // Compare on the three meaningful components — assembly versions usually carry a 0 revision.
            return Normalize(latest) > Normalize(current)
                ? new UpdateInfo(latest, releaseUrl)
                : null;
        }
        catch
        {
            // Offline, rate-limited, malformed JSON, missing repo — all silent.
            return null;
        }
    }

    private static Version Normalize(Version v)
        => new(v.Major, v.Minor, Math.Max(0, v.Build));
}
