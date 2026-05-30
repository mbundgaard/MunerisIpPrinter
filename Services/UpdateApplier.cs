using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;

namespace MunerisIpPrinter.Services;

/// <summary>
/// Downloads a new build of the .exe to %TEMP% and swaps it for the running one.
///
/// The swap can't happen in-process because Windows locks the running .exe.
/// ApplyAndExit spawns a tiny detached cmd helper that waits for the current
/// process to exit, then moves the downloaded file over the original location
/// and relaunches it. The current process exits immediately afterwards.
/// </summary>
public static class UpdateApplier
{
    /// <summary>Streams the release asset to <paramref name="finalPath"/>.partial then atomically
    /// renames on success. Returns true only when the renamed file is in place.
    /// All failure modes (timeout, 404, disk full, AV interference) → false + partial removed.</summary>
    public static async Task<bool> DownloadAsync(string url, string finalPath)
    {
        var partial = finalPath + ".partial";
        try
        {
            // GitHub requires TLS 1.2+; UpdateChecker already sets this, but be defensive
            // in case DownloadAsync runs before any UpdateChecker static ctor on cold path.
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch { }

            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MunerisIpPrinter-Updater");

            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using (var src = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var dst = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await src.CopyToAsync(dst).ConfigureAwait(false);
            }

            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(partial, finalPath);
            return true;
        }
        catch
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { /* ignore */ }
            return false;
        }
    }

    /// <summary>Hands the swap off to a detached cmd.exe and shuts down the app.
    /// The helper waits ~2 s for this process to release the .exe lock, then
    /// move /Y replaces it and start launches the new build.</summary>
    public static void ApplyAndExit(string downloadedExe, string currentExe)
    {
        // /c so cmd exits after the chain finishes.
        // timeout + && chain so we only restart if the move actually succeeded.
        // The empty quoted title after `start` is required when the exe path has spaces,
        // otherwise cmd reads the quoted path as the window title.
        var args = string.Format(
            "/c timeout /t 2 /nobreak >nul && move /Y \"{0}\" \"{1}\" && start \"\" \"{1}\"",
            downloadedExe, currentExe);

        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch
        {
            // Couldn't even spawn the helper — stay running so the user can try the
            // open-release-page link as a manual fallback.
            return;
        }

        Application.Current.Shutdown();
    }
}
