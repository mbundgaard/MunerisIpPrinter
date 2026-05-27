using System.IO;
using MunerisIpPrinter.Models;

namespace MunerisIpPrinter.Infrastructure;

/// <summary>
/// Opt-in diagnostic logging (Settings → Enable logging). Writes a debug.log plus a raw
/// req/resp .bin pair per captured job into a "logs" folder next to the exe.
/// Every method swallows IO errors — logging must never take the app down.
/// </summary>
public sealed class JobLog
{
    private readonly string _dir;
    private readonly string _debugPath;
    private readonly object _lock = new();
    private int _seq;

    public JobLog(string baseDir)
    {
        _dir = Path.Combine(baseDir, "logs");
        _debugPath = Path.Combine(_dir, "debug.log");
        try { Directory.CreateDirectory(_dir); } catch { /* surfaces again on first write */ }
        Line("--- session started ---");
    }

    /// <summary>Appends one timestamped line to debug.log.</summary>
    public void Line(string message)
    {
        var entry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}";
        lock (_lock)
        {
            try { File.AppendAllText(_debugPath, entry); }
            catch { /* logging must never break the app */ }
        }
    }

    /// <summary>Dumps a job's raw request/reply bytes and notes it in debug.log.</summary>
    public void SaveJob(PrintJob job)
    {
        lock (_lock)
        {
            var prefix = $"{job.ReceivedAt:yyyyMMdd_HHmmss}_{job.LocalAddress}_{++_seq:D3}";
            try
            {
                File.WriteAllBytes(Path.Combine(_dir, $"{prefix}_req.bin"), job.Data);
                File.WriteAllBytes(Path.Combine(_dir, $"{prefix}_resp.bin"), job.PrinterReply);
                Line($"saved {prefix}  in={job.Data.Length}B reply={job.PrinterReply.Length}B  remote={job.RemoteEndPoint}");
            }
            catch (Exception ex)
            {
                Line($"job dump failed ({prefix}): {ex.Message}");
            }
        }
    }
}
