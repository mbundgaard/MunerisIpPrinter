using System.IO;
using System.Reflection;
using System.Windows;
using MunerisIpPrinter.Services;

namespace MunerisIpPrinter;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // If a previous session downloaded a newer build, swap it in *now* — before
        // we bind any TCP ports or load settings — so the user just sees the new
        // version come up on next launch with no extra clicks.
        if (TryApplyPendingUpdate())
        {
            // The relauncher is waiting for this process to exit so it can move
            // the temp exe over the current one and start it. Get out of its way.
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    /// <summary>Returns true if a pending update was found and the relauncher was kicked off
    /// (caller must shut down immediately so the swap can complete).</summary>
    private static bool TryApplyPendingUpdate()
    {
        try
        {
            var current = Assembly.GetExecutingAssembly().GetName().Version;
            var currentExe = Assembly.GetEntryAssembly()?.Location;
            if (current == null || string.IsNullOrEmpty(currentExe)) return false;

            // UpdateApplier.DownloadAsync writes to %TEMP%\MunerisIpPrinter-update-<version>.exe.
            // Scan for any such file with a higher version than what's running and apply the highest.
            const string prefix = "MunerisIpPrinter-update-";
            var candidates = Directory.GetFiles(Path.GetTempPath(), prefix + "*.exe");
            Version? bestVer = null;
            string? bestPath = null;
            foreach (var path in candidates)
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var verStr = name.Substring(prefix.Length);
                if (!Version.TryParse(verStr, out var ver)) continue;
                if (ver <= current) continue;
                if (bestVer == null || ver > bestVer)
                {
                    bestVer = ver;
                    bestPath = path;
                }
            }

            if (bestPath == null) return false;

            Relauncher.RelaunchAfterExit(currentExe!, swapFromPath: bestPath);
            return true;
        }
        catch
        {
            // Detection must never block startup — if anything throws, just boot normally.
            return false;
        }
    }
}
