using System.IO;
using MunerisIpPrinter.Models;

namespace MunerisIpPrinter.Infrastructure;

/// <summary>
/// Persists each printer's recent receipts into the shared <see cref="SlotStore"/>.
/// History slots live at key <c>HistoryKeyBase + addressOctet</c>, kept clear of the
/// logo slots (which use the bare octet). Each slot holds a length-prefixed list of jobs.
/// </summary>
public static class PrintHistory
{
    private const int HistoryKeyBase = 1000;
    private const byte Version = 1;

    public static int KeyFor(int addressOctet) => HistoryKeyBase + addressOctet;

    /// <summary>Reads a printer's saved receipts, most-recent-first. Missing/corrupt → empty.</summary>
    public static List<PrintJob> Load(SlotStore store, int addressOctet)
    {
        var jobs = new List<PrintJob>();
        var blob = store.Read(KeyFor(addressOctet));
        if (blob == null || blob.Length == 0) return jobs;

        try
        {
            using var ms = new MemoryStream(blob);
            using var r = new BinaryReader(ms);
            if (r.ReadByte() != Version) return jobs; // unknown layout — ignore
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                int sequence = r.ReadInt32();
                long ticks = r.ReadInt64();
                string remote = r.ReadString();
                string local = r.ReadString();
                byte[] data = r.ReadBytes(r.ReadInt32());
                byte[] reply = r.ReadBytes(r.ReadInt32());
                jobs.Add(new PrintJob
                {
                    Sequence = sequence,
                    ReceivedAt = new DateTime(ticks),
                    RemoteEndPoint = remote,
                    LocalAddress = local,
                    Data = data,
                    PrinterReply = reply,
                });
            }
        }
        catch
        {
            return new List<PrintJob>(); // corrupt slot — start fresh
        }
        return jobs;
    }

    /// <summary>Writes a printer's receipts, in the order given (most-recent-first).</summary>
    public static void Save(SlotStore store, int addressOctet, IReadOnlyList<PrintJob> jobs)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms))
        {
            w.Write(Version);
            w.Write(jobs.Count);
            foreach (var job in jobs)
            {
                w.Write(job.Sequence);
                w.Write(job.ReceivedAt.Ticks);
                w.Write(job.RemoteEndPoint);
                w.Write(job.LocalAddress);
                w.Write(job.Data.Length);
                w.Write(job.Data);
                w.Write(job.PrinterReply.Length);
                w.Write(job.PrinterReply);
            }
        }
        store.Write(KeyFor(addressOctet), ms.ToArray());
    }
}
