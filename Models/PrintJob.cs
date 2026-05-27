namespace MunerisIpPrinter.Models;

public sealed class PrintJob
{
    /// <summary>Per-tab sequence number, assigned by the owning PrinterView.</summary>
    public int Sequence { get; set; }
    public required DateTime ReceivedAt { get; init; }
    public required string RemoteEndPoint { get; init; }
    /// <summary>The local loopback address the connection came in on, e.g. "127.0.0.2" — used to route to a tab.</summary>
    public required string LocalAddress { get; init; }
    public required byte[] Data { get; init; }
    public required byte[] PrinterReply { get; init; }

    public string ShortHeader => $"#{Sequence:D3}";
    public string ShortTime => ReceivedAt.ToString("HH:mm:ss");
}
