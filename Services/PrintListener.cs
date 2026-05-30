using System.IO;
using System.Net;
using System.Net.Sockets;
using MunerisIpPrinter.Infrastructure;
using MunerisIpPrinter.Models;

namespace MunerisIpPrinter.Services;

/// <summary>
/// A single TCP listener on 0.0.0.0:&lt;port&gt;. Every accepted connection is tagged with the
/// local loopback address it came in on (127.0.0.x) so the host can route it to the right tab.
/// Replies to ESC/POS status queries and splits receipts at GS V cuts.
/// </summary>
public sealed class PrintListener
{
    private readonly int _port;
    private readonly SlotStore _slotStore;
    private readonly JobLog? _log;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public event EventHandler<PrintJob>? JobReceived;
    public event EventHandler<string>? StatusChanged;

    public int Port => _port;

    public PrintListener(int port, SlotStore slotStore, JobLog? log = null)
    {
        _port = port;
        _slotStore = slotStore;
        _log = log;
    }

    /// <summary>Binds the port and starts accepting. Throws (e.g. SocketException) if the port is unavailable.</summary>
    public void Start()
    {
        if (_listener != null) return;
        var cts = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Any, _port);
        try
        {
            listener.Start();
        }
        catch
        {
            cts.Dispose();
            throw;
        }
        _cts = cts;
        _listener = listener;
        StatusChanged?.Invoke(this, "Listening");
        _ = AcceptLoop(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
        StatusChanged?.Invoke(this, "Stopped");
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // .NET Framework 4.6.2 has no AcceptTcpClientAsync(CancellationToken) overload —
                // Stop() closes the listener which causes the pending accept to throw, which is
                // how we exit the loop.
                var client = await _listener!.AcceptTcpClientAsync();
                _ = HandleClient(client, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (InvalidOperationException) { return; } // listener.Stop() before pending Accept resolves
            catch (Exception ex) { StatusChanged?.Invoke(this, $"Accept error: {ex.Message}"); }
        }
    }

    private async Task HandleClient(TcpClient client, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        var localAddr = (client.Client.LocalEndPoint as IPEndPoint)?.Address ?? IPAddress.Loopback;
        var localAddrStr = localAddr.ToString();
        var addrBytes = localAddr.GetAddressBytes();
        int slotKey = addrBytes[addrBytes.Length - 1]; // loopback last octet — the per-tab logo slot key
        _log?.Line($"connected {remote} -> {localAddrStr}");

        var reqMs = new MemoryStream();
        var respMs = new MemoryStream();
        var bufferLock = new object();
        int scanPos = 0;

        try
        {
            using var clientStream = client.GetStream();
            var readBuf = new byte[4096];
            while (true)
            {
                int read;
                try { read = await clientStream.ReadAsync(readBuf, 0, readBuf.Length, ct); }
                catch (OperationCanceledException) { break; }
                catch (IOException) { break; }
                if (read <= 0) break;

                lock (bufferLock) { reqMs.Write(readBuf, 0, read); }
                await ProcessEvents(clientStream, reqMs, respMs, bufferLock,
                                    () => scanPos, p => scanPos = p,
                                    remote, localAddrStr, slotKey, ct);
            }
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Session error from {remote}: {ex.Message}");
        }
        finally
        {
            client.Dispose();
            _log?.Line($"disconnected {remote}");
        }

        byte[] finalReq, finalResp;
        lock (bufferLock)
        {
            finalReq = reqMs.ToArray();
            finalResp = respMs.ToArray();
            reqMs.SetLength(0);
            respMs.SetLength(0);
        }
        EmitJob(finalReq, finalResp, remote, localAddrStr, fromCut: false);
    }

    private async Task ProcessEvents(
        NetworkStream clientStream,
        MemoryStream reqMs,
        MemoryStream respMs,
        object bufferLock,
        Func<int> getScanPos,
        Action<int> setScanPos,
        string remote,
        string localAddr,
        int slotKey,
        CancellationToken ct)
    {
        while (true)
        {
            byte[]? pendingReply = null;
            bool didCut = false;
            byte[]? cutReq = null;
            byte[]? cutResp = null;
            byte[]? logoSlot = null;

            lock (bufferLock)
            {
                var buf = reqMs.GetBuffer();
                int len = (int)reqMs.Length;
                int pos = getScanPos();
                if (pos >= len) return;

                var ev = EscPosParser.NextEvent(buf, pos, len);
                if (ev.NeedsMore) return;

                int newPos = pos + ev.Consumed;

                if (!ev.IsCut && ev.Consumed >= 4 && buf[pos] == 0x1D && buf[pos + 1] == 0x2A)
                {
                    byte logoX = buf[pos + 2];
                    byte logoY = buf[pos + 3];
                    int payloadLen = logoX * logoY * 8;
                    if (ev.Consumed == 4 + payloadLen)
                    {
                        // Slot blob: [x][y][bitmap]
                        logoSlot = new byte[2 + payloadLen];
                        logoSlot[0] = logoX;
                        logoSlot[1] = logoY;
                        Array.Copy(buf, pos + 4, logoSlot, 2, payloadLen);
                    }
                }

                if (ev.IsCut)
                {
                    cutReq = new byte[newPos];
                    Array.Copy(buf, 0, cutReq, 0, newPos);
                    cutResp = respMs.ToArray();

                    int tailLen = len - newPos;
                    if (tailLen > 0)
                    {
                        var tail = new byte[tailLen];
                        Array.Copy(buf, newPos, tail, 0, tailLen);
                        reqMs.SetLength(0);
                        reqMs.Write(tail, 0, tailLen);
                    }
                    else
                    {
                        reqMs.SetLength(0);
                    }
                    respMs.SetLength(0);
                    setScanPos(0);
                    didCut = true;
                }
                else
                {
                    setScanPos(newPos);
                    if (ev.Reply.Length > 0)
                    {
                        pendingReply = ev.Reply;
                        respMs.Write(ev.Reply, 0, ev.Reply.Length);
                    }
                }
            }

            if (pendingReply != null)
            {
                try { await clientStream.WriteAsync(pendingReply, 0, pendingReply.Length, ct); }
                catch (IOException) { return; }
                catch (ObjectDisposedException) { return; }
            }

            if (logoSlot != null)
            {
                var slot = logoSlot;
                _ = Task.Run(() =>
                {
                    try
                    {
                        _slotStore.Write(slotKey, slot);
                        _log?.Line($"logo slot {slotKey} updated ({slot.Length} bytes)");
                    }
                    catch (Exception ex) { StatusChanged?.Invoke(this, $"Logo save error: {ex.Message}"); }
                });
            }

            if (didCut)
            {
                EmitJob(cutReq!, cutResp!, remote, localAddr, fromCut: true);
            }
        }
    }

    private void EmitJob(byte[] reqData, byte[] respData, string remote, string localAddr, bool fromCut)
    {
        // Only GS V cut-terminated blocks are real receipts. Status-ping connections
        // and post-cut tails arrive via the EOF path (fromCut == false) — always noise.
        if (!fromCut) return;
        if (reqData.Length == 0) return;

        JobReceived?.Invoke(this, new PrintJob
        {
            ReceivedAt = DateTime.Now,
            RemoteEndPoint = remote,
            LocalAddress = localAddr,
            Data = reqData,
            PrinterReply = respData,
        });
    }
}
