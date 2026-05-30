using System.Net;
using System.Net.Http;
using System.Text;

namespace MunerisIpPrinter.Services;

public sealed record UpdateInfo(Version LatestVersion, string ReleaseUrl, string? AssetUrl);

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
        // GitHub requires TLS 1.2+; .NET Framework 4.6.2 doesn't default to it on older Windows.
        try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
        catch { /* missing on very old Windows — silent */ }

        // GitHub rejects requests with no User-Agent.
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("MunerisIpPrinter-Updater");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>Returns the latest release if it's strictly newer than <paramref name="current"/>, otherwise null.</summary>
    /// <param name="repo">"owner/repo" — e.g. "mbundgaard/MunerisIpPrinter".</param>
    public static async Task<UpdateInfo?> CheckAsync(string repo, Version current, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://api.github.com/repos/{repo}/releases/latest";
            var json = await Http.GetStringAsync(url).ConfigureAwait(false);

            // Tag name and release page URL are top-level fields and appear before any nested
            // author/asset objects, so a "first occurrence wins" extract picks the release-level
            // values without needing a full JSON parser. Avoids a System.Text.Json dependency on net462.
            var tag = ExtractFirstStringField(json, "tag_name");
            var releaseUrl = ExtractFirstStringField(json, "html_url");
            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(releaseUrl)) return null;

            // Tags are conventionally "v1.2.3"; strip the leading v.
            var versionPart = tag!.StartsWith("v") || tag!.StartsWith("V") ? tag!.Substring(1) : tag!;
            if (!Version.TryParse(versionPart, out var latest)) return null;

            if (Normalize(latest) <= Normalize(current)) return null;

            // Try to find the published .exe asset for this version so callers can
            // download it directly instead of sending the user to the release page.
            // Convention: MunerisIpPrinter-<major>.<minor>.<build>.exe.
            var assetName = $"MunerisIpPrinter-{latest.ToString(3)}.exe";
            var assetUrl = FindAssetDownloadUrl(json, assetName);
            return new UpdateInfo(latest, releaseUrl!, assetUrl);
        }
        catch
        {
            // Offline, rate-limited, malformed JSON, missing repo — all silent.
            return null;
        }
    }

    private static Version Normalize(Version v)
        => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>Finds the browser_download_url for the asset whose name matches <paramref name="assetName"/>.
    /// Walks the JSON looking for the "name":"X" pair, then the next browser_download_url after it.
    /// Tolerates GitHub's compact JSON as well as a single-space variant.</summary>
    private static string? FindAssetDownloadUrl(string json, string assetName)
    {
        string[] candidates =
        {
            "\"name\":\"" + assetName + "\"",
            "\"name\": \"" + assetName + "\"",
        };
        int nameIdx = -1;
        foreach (var c in candidates)
        {
            nameIdx = json.IndexOf(c, StringComparison.Ordinal);
            if (nameIdx >= 0) break;
        }
        if (nameIdx < 0) return null;
        return ExtractFirstStringField(json.Substring(nameIdx), "browser_download_url");
    }

    /// <summary>Pulls the first <c>"fieldName": "value"</c> pair out of a JSON document.
    /// Handles \\, \", \n, \t, \r, \/ and \uXXXX escapes inside the value.</summary>
    private static string? ExtractFirstStringField(string json, string fieldName)
    {
        var needle = "\"" + fieldName + "\"";
        int idx = json.IndexOf(needle, StringComparison.Ordinal);
        if (idx < 0) return null;

        int i = idx + needle.Length;
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        if (i >= json.Length || json[i] != ':') return null;
        i++;
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        if (i >= json.Length || json[i] != '"') return null;
        i++;

        var sb = new StringBuilder();
        while (i < json.Length)
        {
            char c = json[i++];
            if (c == '"') return sb.ToString();
            if (c == '\\')
            {
                if (i >= json.Length) return null;
                char e = json[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > json.Length) return null;
                        sb.Append((char)int.Parse(json.Substring(i, 4), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: return null;
                }
            }
            else sb.Append(c);
        }
        return null;
    }
}
