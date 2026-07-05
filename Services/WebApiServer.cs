using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MunerisIpPrinter.Services;

/// <summary>One printer instance as seen by the HTTP API: its number (the loopback last octet /
/// query-param value), display name, and the address a client sends ESC/POS to.</summary>
public readonly struct ApiPrinter
{
    public ApiPrinter(int number, string name, string address)
    {
        Number = number;
        Name = name;
        Address = address;
    }

    public int Number { get; }
    public string Name { get; }
    public string Address { get; }
}

/// <summary>Host callbacks the <see cref="WebApiServer"/> uses to read receipts and mutate
/// settings. Implemented by MainWindow. Every method is called on the UI thread by the server.</summary>
public interface IApiHost
{
    IReadOnlyList<ApiPrinter> ListPrinters();
    byte[]? RenderLatestPng(int number);
    string? LatestText(int number);
    string? LatestHex(int number);

    /// <summary>Clears receipts live (no restart). <paramref name="number"/> 0 = all printers.</summary>
    string ClearReceipts(int number);

    // Mutations save settings and expect the caller to trigger RestartToApply afterwards.
    // They throw on validation errors (bad number, at capacity, last printer, …).
    string AddPrinter(string name);
    string RenamePrinter(int number, string name);
    string RemovePrinter(int number);

    /// <summary>Relaunches the app so saved settings take effect (there is no live reload).</summary>
    void RestartToApply();
}

/// <summary>
/// Local HTTP API, bound to http://localhost:&lt;port&gt;/. Loopback-only, so only processes on
/// this machine can reach it.
///
///   GET  /                          self-describing usage guide
///   GET  /printers                  list printer instances (# / name / address)
///   GET  /latest?printer=N          PNG of printer N's newest receipt (paper only)
///   GET  /latest.txt?printer=N      decoded text of printer N's newest receipt
///   GET  /screenshot                PNG of the whole app window
///   POST /printers/add?name=...             add a printer, then restart
///   POST /printers/rename?printer=N&amp;name=...  rename printer N, then restart
///   POST /printers/remove?printer=N         remove printer N, then restart
/// </summary>
public sealed class WebApiServer : IDisposable
{
    public int Port { get; }
    private readonly HttpListener _listener = new();
    private readonly Window _window;
    private readonly IApiHost _host;
    private readonly CancellationTokenSource _cts = new();

    public WebApiServer(Window window, int port, IApiHost host)
    {
        _window = window;
        _host = host;
        Port = port;
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { return; }
            _ = HandleAsync(ctx, ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            switch (path)
            {
                case "/":                 SendGuide(ctx); break;
                case "/printers":         SendPrinters(ctx); break;
                case "/latest":           await SendLatestPngAsync(ctx).ConfigureAwait(false); break;
                case "/latest.txt":       SendLatestText(ctx); break;
                case "/latest.hex":       SendLatestHex(ctx); break;
                case "/screenshot":       await SendScreenshotAsync(ctx).ConfigureAwait(false); break;
                case "/clear":            HandleAction(ctx, h => h.ClearReceipts(OptionalInt(ctx, "printer", 0))); break;
                case "/printers/add":     HandleMutation(ctx, h => h.AddPrinter(Query(ctx, "name"))); break;
                case "/printers/rename":  HandleMutation(ctx, h => h.RenamePrinter(QueryInt(ctx, "printer"), Query(ctx, "name"))); break;
                case "/printers/remove":  HandleMutation(ctx, h => h.RemovePrinter(QueryInt(ctx, "printer"))); break;
                default:
                    WriteText(ctx, 404, "Not found. GET / for the list of endpoints.");
                    break;
            }
        }
        catch (Exception ex)
        {
            try
            {
                ctx.Response.StatusCode = 500;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                var bytes = Encoding.UTF8.GetBytes(ex.ToString());
                await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
            }
            catch { }
            try { ctx.Response.Close(); } catch { }
        }
    }

    // ---- Reads ---------------------------------------------------------

    private void SendGuide(HttpListenerContext ctx)
    {
        var printers = _window.Dispatcher.Invoke(() => _host.ListPrinters());
        var sb = new StringBuilder();
        sb.AppendLine("MunerisIpPrinter — local ESC/POS receipt-printer emulator");
        sb.AppendLine("==========================================================");
        sb.AppendLine();
        sb.AppendLine("Each \"printer\" is a loopback address listening for raw ESC/POS on TCP port 9100.");
        sb.AppendLine("Send receipt bytes to one and this app renders the receipt on screen; fetch the");
        sb.AppendLine($"rendered image or decoded text back over this HTTP API (port {Port}).");
        sb.AppendLine();
        sb.AppendLine($"PRINTERS ({printers.Count}):");
        sb.AppendLine("  #   name                 send ESC/POS to");
        foreach (var p in printers)
            sb.AppendLine($"  {p.Number,-3} {Trunc(p.Name, 20),-20} {p.Address}:9100");
        sb.AppendLine("  (# is the value for the ?printer= query param below.)");
        sb.AppendLine();
        sb.AppendLine("DESIGN LOOP:");
        sb.AppendLine("  1. Build an ESC/POS byte stream. End it with a cut so it renders as a finished");
        sb.AppendLine("     receipt:  GS V 0  = bytes  1D 56 00.");
        sb.AppendLine("  2. Open a TCP connection to 127.0.0.<#>:9100 and write the bytes.");
        sb.AppendLine("  3. GET /latest?printer=<#>      -> PNG of that printer's newest receipt");
        sb.AppendLine("     GET /latest.txt?printer=<#>  -> its decoded text");
        sb.AppendLine("     GET /latest.hex?printer=<#>  -> its exact received bytes (hex)");
        sb.AppendLine("  4. Inspect, adjust the bytes, repeat.");
        sb.AppendLine();
        sb.AppendLine("ENDPOINTS:");
        sb.AppendLine("  GET  /                        this guide");
        sb.AppendLine("  GET  /printers               list printers (# / name / address)");
        sb.AppendLine("  GET  /latest?printer=<#>      PNG of the newest receipt for printer #");
        sb.AppendLine("  GET  /latest.txt?printer=<#>  decoded text of the newest receipt");
        sb.AppendLine("  GET  /latest.hex?printer=<#>  exact received bytes as space-separated hex");
        sb.AppendLine("  GET  /screenshot             PNG of the whole app window");
        sb.AppendLine("  POST /clear                  clear all receipts (or ?printer=<#> for one)");
        sb.AppendLine("  POST /printers/add?name=<name>");
        sb.AppendLine("  POST /printers/rename?printer=<#>&name=<name>");
        sb.AppendLine("  POST /printers/remove?printer=<#>");
        sb.AppendLine();
        sb.AppendLine("SETTINGS CHANGES:");
        sb.AppendLine("  The /printers POST endpoints (add/rename/remove) save settings and RESTART the");
        sb.AppendLine("  app (there is no live reload). The response returns first; the restart follows.");
        sb.AppendLine("  Wait ~5 seconds, then GET / again to see the updated printer list.");
        sb.AppendLine("  (POST /clear is live and does NOT restart.)");
        sb.AppendLine();
        sb.AppendLine("NOTES:");
        sb.AppendLine("  - Loopback-only (127.0.0.1); reachable only from this machine.");
        sb.AppendLine("  - ESC/POS text defaults to code page 437; switch with ESC t.");
        WriteText(ctx, 200, sb.ToString());
    }

    private void SendPrinters(HttpListenerContext ctx)
    {
        var printers = _window.Dispatcher.Invoke(() => _host.ListPrinters());
        var sb = new StringBuilder();
        sb.AppendLine("#   name                 address");
        foreach (var p in printers)
            sb.AppendLine($"{p.Number,-3} {Trunc(p.Name, 20),-20} {p.Address}:9100");
        WriteText(ctx, 200, sb.ToString());
    }

    private async Task SendLatestPngAsync(HttpListenerContext ctx)
    {
        if (!TryQueryInt(ctx, "printer", out int number))
        {
            WriteText(ctx, 400, "Missing or invalid 'printer' query param (e.g. /latest?printer=1).");
            return;
        }

        byte[]? png = await _window.Dispatcher.InvokeAsync(() => _host.RenderLatestPng(number)).Task.ConfigureAwait(false);
        if (png == null)
        {
            WriteText(ctx, 404, $"No receipt for printer {number} yet (or no such printer). GET /printers to list them.");
            return;
        }

        ctx.Response.ContentType = "image/png";
        ctx.Response.ContentLength64 = png.Length;
        await ctx.Response.OutputStream.WriteAsync(png, 0, png.Length).ConfigureAwait(false);
        ctx.Response.Close();
    }

    private void SendLatestText(HttpListenerContext ctx)
    {
        if (!TryQueryInt(ctx, "printer", out int number))
        {
            WriteText(ctx, 400, "Missing or invalid 'printer' query param (e.g. /latest.txt?printer=1).");
            return;
        }

        string? text = _window.Dispatcher.Invoke(() => _host.LatestText(number));
        if (text == null)
        {
            WriteText(ctx, 404, $"No receipt for printer {number} yet (or no such printer). GET /printers to list them.");
            return;
        }
        WriteText(ctx, 200, text);
    }

    private void SendLatestHex(HttpListenerContext ctx)
    {
        if (!TryQueryInt(ctx, "printer", out int number))
        {
            WriteText(ctx, 400, "Missing or invalid 'printer' query param (e.g. /latest.hex?printer=1).");
            return;
        }

        string? hex = _window.Dispatcher.Invoke(() => _host.LatestHex(number));
        if (hex == null)
        {
            WriteText(ctx, 404, $"No receipt for printer {number} yet (or no such printer). GET /printers to list them.");
            return;
        }
        WriteText(ctx, 200, hex);
    }

    private async Task SendScreenshotAsync(HttpListenerContext ctx)
    {
        byte[] png = await _window.Dispatcher.InvokeAsync(() =>
        {
            int w = Math.Max(1, (int)_window.ActualWidth);
            int h = Math.Max(1, (int)_window.ActualHeight);
            var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(_window);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }).Task.ConfigureAwait(false);

        ctx.Response.ContentType = "image/png";
        ctx.Response.ContentLength64 = png.Length;
        await ctx.Response.OutputStream.WriteAsync(png, 0, png.Length).ConfigureAwait(false);
        ctx.Response.Close();
    }

    // ---- Mutations -----------------------------------------------------

    /// <summary>A POST that runs a live host action and returns its message — no restart, unlike
    /// <see cref="HandleMutation"/>.</summary>
    private void HandleAction(HttpListenerContext ctx, Func<IApiHost, string> op)
    {
        if (ctx.Request.HttpMethod != "POST")
        {
            WriteText(ctx, 405, "Use POST for this endpoint.");
            return;
        }

        string message;
        try { message = _window.Dispatcher.Invoke(() => op(_host)); }
        catch (Exception ex)
        {
            WriteText(ctx, 400, "Rejected: " + ex.Message);
            return;
        }
        WriteText(ctx, 200, message);
    }

    private void HandleMutation(HttpListenerContext ctx, Func<IApiHost, string> op)
    {
        if (ctx.Request.HttpMethod != "POST")
        {
            WriteText(ctx, 405, "Use POST — this endpoint changes settings and restarts the app.");
            return;
        }

        string message;
        try { message = _window.Dispatcher.Invoke(() => op(_host)); }
        catch (Exception ex)
        {
            WriteText(ctx, 400, "Rejected: " + ex.Message);
            return;
        }

        WriteText(ctx, 200, message +
            "\n\nApplying now — the app is restarting. Wait ~5 seconds, then GET / again.");

        // Restart only after the response is flushed; queued on the UI thread so this handler
        // returns (and the socket closes) before Application.Shutdown tears the listener down.
        _ = _window.Dispatcher.BeginInvoke(new Action(() => _host.RestartToApply()));
    }

    // ---- Helpers -------------------------------------------------------

    private static string Query(HttpListenerContext ctx, string key)
        => ctx.Request.QueryString[key] ?? string.Empty;

    private static int QueryInt(HttpListenerContext ctx, string key)
        => TryQueryInt(ctx, key, out int v)
            ? v
            : throw new ArgumentException($"Missing or invalid '{key}' query param.");

    private static bool TryQueryInt(HttpListenerContext ctx, string key, out int value)
        => int.TryParse(ctx.Request.QueryString[key], out value);

    private static int OptionalInt(HttpListenerContext ctx, string key, int fallback)
        => TryQueryInt(ctx, key, out int v) ? v : fallback;

    private static string Trunc(string s, int max)
        => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s.Substring(0, max - 1) + "…");

    private static void WriteText(HttpListenerContext ctx, int status, string body)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        _cts.Dispose();
    }
}
