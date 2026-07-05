using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MunerisIpPrinter.Services;

namespace MunerisIpPrinter.Infrastructure;

/// <summary>
/// Walks a raw ESC/POS byte stream and builds a WPF FlowDocument that reflects
/// bold, alignment, size (GS ! and ESC ! flags), underline, reverse (GS B),
/// codepage switches (ESC t), inline raster bitmaps (GS v 0), stored logos
/// (GS *, GS /), and QR codes (GS ( k). Handles DLE SI/SO transparent wrappers
/// and ESC @ initialise so state doesn't bleed between logical prints.
///
/// The plain-text extractor (<see cref="Services.EscPosTextExtractor"/>) still
/// runs in parallel for the copy-as-text path — this renderer is only for
/// the on-screen display.
/// </summary>
public static class EscPosRenderer
{
    private const double BaseFontSize = 11;

    /// <summary>Builds a fully-styled FlowDocument. <paramref name="slotStore"/>
    /// and <paramref name="slotKey"/> are used to resolve GS / (print stored logo)
    /// against the persisted per-printer logo slot.</summary>
    public static FlowDocument Render(byte[] data, SlotStore? slotStore, int slotKey, int defaultCodePage = 437)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = BaseFontSize,
            Foreground = Brushes.Black,
            Background = Brushes.Transparent,
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left,
        };
        TextOptions.SetTextFormattingMode(doc, TextFormattingMode.Display);

        var ctx = new RenderContext(doc, defaultCodePage);
        int i = 0;
        while (i < data.Length)
        {
            byte b = data[i];

            if (b == 0x0A) // LF
            {
                ctx.FlushText();
                ctx.NewLine();
                i++;
                continue;
            }
            if (b == 0x0D) { i++; continue; } // CR — ignore (LF closes the line)
            if (b == 0x09) { ctx.TextBuffer.Add((byte)' '); i++; continue; } // TAB
            if (b == 0x0C) { ctx.FlushText(); ctx.NewLine(); i++; continue; } // FF

            if (b >= 0x20)
            {
                ctx.TextBuffer.Add(b);
                i++;
                continue;
            }

            ctx.FlushText();
            i = HandleControl(data, i, ctx, slotStore, slotKey);
        }
        ctx.FlushText();
        ctx.TrimTrailingEmpty();
        return doc;
    }

    // ---- Command dispatch ---------------------------------------------

    private static int HandleControl(byte[] data, int pos, RenderContext ctx, SlotStore? slots, int slotKey) =>
        data[pos] switch
        {
            0x1B => HandleEsc(data, pos, ctx),
            0x1D => HandleGs(data, pos, ctx, slots, slotKey),
            0x1C => HandleFs(data, pos),
            0x10 => HandleDle(data, pos),
            _ => pos + 1,
        };

    private static int HandleEsc(byte[] data, int pos, RenderContext ctx)
    {
        if (pos + 1 >= data.Length) return data.Length;
        byte cmd = data[pos + 1];
        switch (cmd)
        {
            case 0x40: ctx.Reset(); return pos + 2;                          // ESC @
            case 0x61: // ESC a n — alignment
                if (pos + 2 < data.Length)
                {
                    ctx.SetAlign(data[pos + 2] switch
                    {
                        1 => TextAlignment.Center,
                        2 => TextAlignment.Right,
                        _ => TextAlignment.Left,
                    });
                }
                return pos + 3;
            case 0x45: // ESC E n — bold
                if (pos + 2 < data.Length) ctx.Bold = data[pos + 2] != 0;
                return pos + 3;
            case 0x2D: // ESC - n — underline
                if (pos + 2 < data.Length) ctx.Underline = data[pos + 2] != 0;
                return pos + 3;
            case 0x74: // ESC t n — codepage
                if (pos + 2 < data.Length) ctx.SetCodePage(data[pos + 2]);
                return pos + 3;
            case 0x21: // ESC ! n — print mode flag byte
                if (pos + 2 < data.Length)
                {
                    byte n = data[pos + 2];
                    ctx.Bold = (n & 0x08) != 0;
                    ctx.HeightMul = (n & 0x10) != 0 ? 2 : 1;
                    ctx.WidthMul = (n & 0x20) != 0 ? 2 : 1;
                    ctx.Underline = (n & 0x80) != 0;
                }
                return pos + 3;
            case 0x2A: // ESC * m nL nH d1..dN (old bit image; skip)
                if (pos + 4 < data.Length)
                {
                    int len = data[pos + 3] + data[pos + 4] * 256;
                    return System.Math.Min(pos + 5 + len, data.Length);
                }
                return data.Length;
            case 0x70: return System.Math.Min(pos + 5, data.Length); // ESC p m t1 t2 — drawer pulse
            case 0x63: return System.Math.Min(pos + 4, data.Length); // ESC c 3/4/5 — control-panel
            case 0x24: return System.Math.Min(pos + 4, data.Length); // ESC $ — set absolute position
            case 0x5C: return System.Math.Min(pos + 4, data.Length); // ESC \ — set relative position
            case 0x33: return System.Math.Min(pos + 3, data.Length); // ESC 3 n — line spacing
            case 0x32: return pos + 2;                               // ESC 2 — default line spacing
            case 0x64: return System.Math.Min(pos + 3, data.Length); // ESC d n — feed lines
            case 0x4A: return System.Math.Min(pos + 3, data.Length); // ESC J n — feed dots
            case 0x66: return System.Math.Min(pos + 4, data.Length); // ESC f
            case 0x69: return pos + 2;                               // ESC i — cut
            case 0x6D: return pos + 2;                               // ESC m — partial cut
            default: return System.Math.Min(pos + 3, data.Length);   // conservative 1-param skip
        }
    }

    private static int HandleGs(byte[] data, int pos, RenderContext ctx, SlotStore? slots, int slotKey)
    {
        if (pos + 1 >= data.Length) return data.Length;
        byte cmd = data[pos + 1];
        switch (cmd)
        {
            case 0x21: // GS ! n — size
                if (pos + 2 < data.Length)
                {
                    byte n = data[pos + 2];
                    ctx.WidthMul = ((n >> 4) & 0x07) + 1;
                    ctx.HeightMul = (n & 0x07) + 1;
                }
                return pos + 3;
            case 0x42: // GS B n — reverse
                if (pos + 2 < data.Length) ctx.Reverse = data[pos + 2] != 0;
                return pos + 3;
            case 0x56: // GS V — cut (should have been split by EscPosParser upstream)
                if (pos + 2 >= data.Length) return data.Length;
                byte m = data[pos + 2];
                return System.Math.Min(pos + (m is 65 or 66 or 97 or 98 ? 4 : 3), data.Length);
            case 0x76: return RenderRaster(data, pos, ctx);                       // GS v 0 — inline raster
            case 0x2F: return RenderStoredLogo(data, pos, ctx, slots, slotKey);   // GS / m — print stored logo
            case 0x2A: // GS * x y data — define downloaded bit image
                if (pos + 3 < data.Length)
                {
                    int payload = data[pos + 2] * data[pos + 3] * 8;
                    return System.Math.Min(pos + 4 + payload, data.Length);
                }
                return data.Length;
            case 0x28: return HandleGsExtended(data, pos, ctx);                    // GS ( — extended (QR / PDF417)
            case 0x50: return System.Math.Min(pos + 4, data.Length); // GS P xdpi ydpi
            case 0x4C: return System.Math.Min(pos + 4, data.Length); // GS L nL nH — left margin
            case 0x57: return System.Math.Min(pos + 4, data.Length); // GS W nL nH — print area width
            case 0x66: return System.Math.Min(pos + 3, data.Length); // GS f n — barcode font
            case 0x68: return System.Math.Min(pos + 3, data.Length); // GS h n — barcode height
            case 0x77: return System.Math.Min(pos + 3, data.Length); // GS w n — barcode module width
            case 0x48: return System.Math.Min(pos + 3, data.Length); // GS H n — HRI position
            default: return System.Math.Min(pos + 3, data.Length);
        }
    }

    private static int HandleGsExtended(byte[] data, int pos, RenderContext ctx)
    {
        // GS ( <fn> pL pH <cn> <sub-fn> <params...>
        if (pos + 4 >= data.Length) return data.Length;
        byte kind = data[pos + 2];
        int pL = data[pos + 3];
        int pH = data[pos + 4];
        int payload = pL + pH * 256;
        int end = System.Math.Min(pos + 5 + payload, data.Length);
        if (kind == 0x6B && payload >= 2) // 'k' — QR / PDF417 / etc.
        {
            byte cn = data[pos + 5];
            if (cn == 49) HandleQrCommand(data, pos + 5, payload, ctx);
        }
        return end;
    }

    private static void HandleQrCommand(byte[] data, int start, int payload, RenderContext ctx)
    {
        // At start: cn, fn, then function-specific params.
        if (payload < 2) return;
        byte fn = data[start + 1];
        switch (fn)
        {
            case 65: /* model select — accept */ break;
            case 67: if (payload >= 3) ctx.QrModuleSize = data[start + 2]; break;
            case 69: if (payload >= 3)
                {
                    ctx.QrEcc = data[start + 2] switch { 48 => 'L', 50 => 'Q', 51 => 'H', _ => 'M' };
                }
                break;
            case 80: // store data — m at offset 2, data from offset 3
                if (payload >= 3)
                {
                    int dataLen = payload - 3;
                    ctx.QrData = new byte[dataLen];
                    if (dataLen > 0) System.Array.Copy(data, start + 3, ctx.QrData, 0, dataLen);
                }
                break;
            case 81: // print
                if (ctx.QrData != null && ctx.QrData.Length > 0)
                {
                    try
                    {
                        var bmp = QrEncoder.Encode(ctx.QrData, ctx.QrModuleSize, ctx.QrEcc);
                        ctx.AddImage(bmp);
                    }
                    catch { /* payload too large / encoding failure — skip silently */ }
                    ctx.QrData = null;
                }
                break;
        }
    }

    private static int HandleFs(byte[] data, int pos)
    {
        if (pos + 1 >= data.Length) return data.Length;
        byte cmd = data[pos + 1];
        return cmd switch
        {
            0x26 or 0x2E => pos + 2,       // FS & / FS . — multi-byte on/off (ignored, we don't handle CJK-2byte yet)
            0x70 => System.Math.Min(pos + 4, data.Length),   // FS p m n
            _ => System.Math.Min(pos + 3, data.Length),
        };
    }

    private static int HandleDle(byte[] data, int pos)
    {
        if (pos + 1 >= data.Length) return data.Length;
        byte cmd = data[pos + 1];
        return cmd switch
        {
            0x0F or 0x0E => pos + 2,       // DLE SI / SO — transparent wrappers, skip cleanly
            0x04 or 0x05 => System.Math.Min(pos + 3, data.Length), // DLE EOT/ENQ n
            0x14 => System.Math.Min(pos + 5, data.Length),        // DLE DC4 m a b
            _ => pos + 2,
        };
    }

    // ---- Image handlers ----------------------------------------------

    private static int RenderRaster(byte[] data, int pos, RenderContext ctx)
    {
        // GS v 0 m xL xH yL yH d1..dN
        if (pos + 7 >= data.Length) return data.Length;
        byte mode = data[pos + 3];
        int xL = data[pos + 4], xH = data[pos + 5];
        int yL = data[pos + 6], yH = data[pos + 7];
        int widthBytes = xL + xH * 256;
        int height = yL + yH * 256;
        int payload = widthBytes * height;
        int end = pos + 8 + payload;
        if (widthBytes <= 0 || height <= 0 || end > data.Length) return System.Math.Min(end, data.Length);

        var bmp = RasterToBitmap(data, pos + 8, widthBytes, height);
        ctx.AddRasterImage(bmp, mode);
        return end;
    }

    private static int RenderStoredLogo(byte[] data, int pos, RenderContext ctx, SlotStore? slots, int slotKey)
    {
        if (slots != null)
        {
            var bmp = LogoBitmap.FromSlotBytes(slots.Read(slotKey));
            if (bmp != null) ctx.AddImage(bmp);
        }
        return System.Math.Min(pos + 3, data.Length);
    }

    private static BitmapSource RasterToBitmap(byte[] data, int offset, int widthBytes, int height)
    {
        int width = widthBytes * 8;
        var pixels = new byte[width * height];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = 255;
        for (int y = 0; y < height; y++)
        {
            int row = offset + y * widthBytes;
            for (int xB = 0; xB < widthBytes; xB++)
            {
                byte b = data[row + xB];
                if (b == 0) continue;
                int outBase = y * width + xB * 8;
                for (int bit = 0; bit < 8; bit++)
                    if (((b >> (7 - bit)) & 1) != 0) pixels[outBase + bit] = 0;
            }
        }
        // 203 DPI = typical Epson thermal head; makes raster width settle at the
        // paper column's physical scale instead of 2× wider (see PrinterView paper sizing).
        var bmp = BitmapSource.Create(width, height, 203, 203, PixelFormats.Gray8, null, pixels, width);
        bmp.Freeze();
        return bmp;
    }

    // ---- State + FlowDocument helpers ---------------------------------

    private sealed class RenderContext
    {
        public FlowDocument Doc;
        public List<byte> TextBuffer = new();
        public readonly int DefaultCodePage;
        public int CodePage;
        public bool Bold, Underline, Reverse;
        public int WidthMul = 1, HeightMul = 1;
        public TextAlignment Align = TextAlignment.Left;
        public Paragraph CurrentPara;

        // QR state accumulated between GS ( k sub-commands.
        public int QrModuleSize = 4;
        public char QrEcc = 'M';
        public byte[]? QrData;

        public RenderContext(FlowDocument doc, int defaultCodePage)
        {
            Doc = doc;
            DefaultCodePage = defaultCodePage;
            CodePage = defaultCodePage;
            CurrentPara = NewParagraph();
            doc.Blocks.Add(CurrentPara);
        }

        public void FlushText()
        {
            if (TextBuffer.Count == 0) return;
            string text;
            try { text = Encoding.GetEncoding(CodePage).GetString(TextBuffer.ToArray()); }
            catch { text = Encoding.GetEncoding(28591).GetString(TextBuffer.ToArray()); }
            TextBuffer.Clear();

            var run = new Run(text);
            if (Bold) run.FontWeight = FontWeights.Bold;
            if (Underline) run.TextDecorations = TextDecorations.Underline;
            if (Reverse) { run.Background = Brushes.Black; run.Foreground = Brushes.White; }
            int scale = System.Math.Max(HeightMul, WidthMul);
            if (scale > 1) run.FontSize = BaseFontSize * scale;
            CurrentPara.Inlines.Add(run);
        }

        public void NewLine()
        {
            CurrentPara = NewParagraph();
            Doc.Blocks.Add(CurrentPara);
        }

        public void AddImage(BitmapSource bmp) => AddRasterImage(bmp, 0);

        public void AddRasterImage(BitmapSource bmp, byte mode)
        {
            FlushText();
            // Close the current paragraph and start a fresh block for the image so
            // its centring / left align matches the current state.
            if (CurrentPara.Inlines.Count == 0) Doc.Blocks.Remove(CurrentPara);

            var img = new Image
            {
                Source = bmp,
                Stretch = Stretch.None,
                HorizontalAlignment = ToHAlign(Align),
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

            var block = new BlockUIContainer(img) { Margin = new Thickness(0) };
            Doc.Blocks.Add(block);

            CurrentPara = NewParagraph();
            Doc.Blocks.Add(CurrentPara);
        }

        public void SetAlign(TextAlignment a)
        {
            if (Align == a) return;
            Align = a;
            if (CurrentPara.Inlines.Count == 0) CurrentPara.TextAlignment = a;
            else { CurrentPara = NewParagraph(); Doc.Blocks.Add(CurrentPara); }
        }

        public void SetCodePage(int n)
        {
            CodePage = n switch
            {
                0 => 437, 1 => 932, 2 => 850, 3 => 860, 4 => 863, 5 => 865,
                11 => 851, 13 => 857, 14 => 737, 15 => 28597, 16 => 1252,
                17 => 866, 18 => 852, 19 => 858, 33 => 862, 34 => 864,
                _ => CodePage,
            };
        }

        public void Reset()
        {
            FlushText();
            Bold = Underline = Reverse = false;
            WidthMul = HeightMul = 1;
            Align = TextAlignment.Left;
            CodePage = DefaultCodePage; // ESC @ returns to the printer's configured default page
            QrData = null;
            QrEcc = 'M';
            QrModuleSize = 4;
            if (CurrentPara.Inlines.Count == 0) CurrentPara.TextAlignment = Align;
            else { CurrentPara = NewParagraph(); Doc.Blocks.Add(CurrentPara); }
        }

        public void TrimTrailingEmpty()
        {
            while (Doc.Blocks.LastBlock is Paragraph p && p.Inlines.Count == 0)
                Doc.Blocks.Remove(p);
        }

        private Paragraph NewParagraph() => new()
        {
            TextAlignment = Align,
            Margin = new Thickness(0),
        };

        private static HorizontalAlignment ToHAlign(TextAlignment a) => a switch
        {
            TextAlignment.Center => HorizontalAlignment.Center,
            TextAlignment.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Left,
        };
    }
}
