using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MunerisIpPrinter.Services;

/// <summary>
/// Local HTTP API. Bound to http://localhost:&lt;port&gt;/.
///
///   GET  /            plain-text route index
///   GET  /screenshot  PNG of the MainWindow as currently rendered
/// </summary>
public sealed class WebApiServer : IDisposable
{
    public int Port { get; }
    private readonly HttpListener _listener = new();
    private readonly Window _window;
    private readonly CancellationTokenSource _cts = new();

    public WebApiServer(Window window, int port)
    {
        _window = window;
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
                case "/screenshot": await SendScreenshotAsync(ctx).ConfigureAwait(false); break;
                case "/":           SendIndex(ctx); break;
                default:
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
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
                await ctx.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
            }
            catch { }
            try { ctx.Response.Close(); } catch { }
        }
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
        await ctx.Response.OutputStream.WriteAsync(png).ConfigureAwait(false);
        ctx.Response.Close();
    }

    private void SendIndex(HttpListenerContext ctx)
    {
        const string body =
            "MunerisIpPrinter local API\n" +
            "  GET /screenshot   PNG of the WPF window\n";
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentType = "text/plain; charset=utf-8";
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
