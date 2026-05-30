using System.Diagnostics;
using System.Text;

namespace MunerisIpPrinter.Services;

/// <summary>
/// Spawns a detached PowerShell helper that <c>Wait-Process</c>es on the current PID, optionally
/// swaps a downloaded .exe into place, then launches the target. Replaces the older
/// "sleep N seconds and hope the old instance is gone" pattern, which lost the race when the
/// new instance came up before the old one had released the TCP port.
/// </summary>
public static class Relauncher
{
    /// <summary>Wait for this process to exit, then start <paramref name="targetExe"/>.
    /// If <paramref name="swapFromPath"/> is given, move it over <paramref name="targetExe"/>
    /// after the wait but before the launch (the auto-update path).</summary>
    public static void RelaunchAfterExit(string targetExe, string? swapFromPath = null)
    {
        int pid = Process.GetCurrentProcess().Id;
        var script = new StringBuilder();
        script.Append($"Wait-Process -Id {pid} -ErrorAction SilentlyContinue;");
        if (!string.IsNullOrEmpty(swapFromPath))
            script.Append($" Move-Item -Force -LiteralPath {QuotePs(swapFromPath!)} -Destination {QuotePs(targetExe)};");
        script.Append($" Start-Process -FilePath {QuotePs(targetExe)}");

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -WindowStyle Hidden -Command \"& {{ {script} }}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch { /* if we can't spawn the helper, the caller's shutdown leaves the user without an app — but we have no recovery path */ }
    }

    /// <summary>PowerShell single-quoted string with doubled-up internal apostrophes
    /// (e.g. <c>C:\Users\O'Brien\…</c> stays valid).</summary>
    private static string QuotePs(string s) => "'" + s.Replace("'", "''") + "'";
}
