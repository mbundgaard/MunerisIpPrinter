using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MunerisIpPrinter.Infrastructure;
using MunerisIpPrinter.Models;

namespace MunerisIpPrinter.Services;

/// <summary>
/// Binds one TCP listener per configured loopback address (127.0.0.1, .2, …) on the same port.
/// Loopback-only listeners don't trigger the Windows Defender Firewall prompt the way an
/// <see cref="IPAddress.Any"/> bind does. Each accepted connection is tagged with the local
/// loopback address it came in on so the host can route it to the right printer view.
/// Replies to ESC/POS status queries and splits receipts at GS V cuts.
/// </summary>
public sealed class PrintListener
{
    private readonly int _port;
    private readonly IPAddress[] _addresses;
    private readonly SlotStore _slotStore;
    private readonly JobLog? _log;
    private readonly List<TcpListener> _listeners = new();
    private CancellationTokenSource? _cts;

    public event EventHandler<PrintJob>? JobReceived;
    public event EventHandler<string>? StatusChanged;

    /// <summary>Raised on every TCP accept, with the local loopback address the connection
    /// came in on. Lets the UI flash a "traffic" indicator even for connections that don't
    /// end up emitting a printable receipt (POS status pings, half-open probes, etc.).</summary>
    public event EventHandler<string>? ConnectionActivity;

    public int Port => _port;

    public PrintListener(int port, IEnumerable<IPAddress> addresses, SlotStore slotStore, JobLog? log = null)
    {
        _port = port;
        _addresses = addresses.ToArray();
        _slotStore = slotStore;
        _log = log;
    }

    /// <summary>Binds one socket per configured loopback address. Retries briefly on
    /// AddressAlreadyInUse so a restart (settings save, auto-update apply) can take over
    /// the ports the prior instance is in the process of releasing.</summary>
    public void Start()
    {
        if (_listeners.Count > 0) return;
        var cts = new CancellationTokenSource();
        var listeners = new List<TcpListener>();
        try
        {
            foreach (var addr in _addresses)
                listeners.Add(BindWithRetry(addr, _port, attempts: 8, delayMs: 500));
        }
        catch
        {
            foreach (var l in listeners) try { l.Stop(); } catch { /* best effort */ }
            cts.Dispose();
            throw;
        }
        _cts = cts;
        _listeners.AddRange(listeners);
        StatusChanged?.Invoke(this, $"Listening on {_listeners.Count} loopback address(es)");
        foreach (var l in _listeners) _ = AcceptLoop(l, _cts.Token);
    }

    /// <summary>Tries to start a listener up to <paramref name="attempts"/> times, waiting
    /// <paramref name="delayMs"/> between tries when the port is still held. Total max wait
    /// = attempts × delayMs (~4 s by default).</summary>
    private static TcpListener BindWithRetry(IPAddress addr, int port, int attempts, int delayMs)
    {
        SocketException? last = null;
        for (int i = 0; i < attempts; i++)
        {
            var l = new TcpListener(addr, port);
            try
            {
                l.Start();
                return l;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                last = ex;
                try { l.Stop(); } catch { /* ignore */ }
                if (i < attempts - 1) Thread.Sleep(delayMs);
            }
        }
        throw last!;
    }

    public void Stop()
    {
        _cts?.Cancel();
        foreach (var l in _listeners) try { l.Stop(); } catch { /* best effort */ }
        _listeners.Clear();
        StatusChanged?.Invoke(this, "Stopped");
    }

    private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // .NET Framework 4.6.2 has no AcceptTcpClientAsync(CancellationToken) overload —
                // Stop() closes the listener which causes the pending accept to throw, which is
                // how we exit the loop.
                var client = await listener.AcceptTcpClientAsync();
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
        ConnectionActivity?.Invoke(this, localAddrStr);

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
