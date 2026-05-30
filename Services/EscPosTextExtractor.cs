using System.Text;

namespace MunerisIpPrinter.Services;

public static class EscPosTextExtractor
{
    private static readonly Dictionary<int, int> EscCodePageMap = new()
    {
        { 0, 437 },     // PC437 USA
        { 1, 932 },     // Katakana (approx via Shift-JIS)
        { 2, 850 },     // PC850 Multilingual
        { 3, 860 },     // PC860 Portuguese
        { 4, 863 },     // PC863 Canadian-French
        { 5, 865 },     // PC865 Nordic
        { 11, 851 },    // PC851 Greek
        { 13, 857 },    // PC857 Turkish
        { 14, 737 },    // PC737 Greek
        { 15, 28597 },  // ISO8859-7
        { 16, 1252 },   // Windows-1252
        { 17, 866 },    // PC866 Cyrillic 2
        { 18, 852 },    // PC852 Latin 2
        { 19, 858 },    // PC858 Euro
        { 33, 862 },    // PC862 Hebrew
        { 34, 864 },    // PC864 Arabic
    };

    // .NET Framework 4.6.2 ships every codepage in the BCL — no provider registration needed.

    public static string Extract(byte[] data)
    {
        var sb = new StringBuilder(data.Length);
        var textBuf = new List<byte>();
        var currentCp = 437;

        void FlushText()
        {
            if (textBuf.Count == 0) return;
            try
            {
                sb.Append(Encoding.GetEncoding(currentCp).GetString(textBuf.ToArray()));
            }
            catch
            {
                sb.Append(Encoding.GetEncoding(28591).GetString(textBuf.ToArray())); // ISO-8859-1 (Latin-1)
            }
            textBuf.Clear();
        }

        int i = 0;
        while (i < data.Length)
        {
            var b = data[i];

            if (b == 0x0A || b == 0x0D || b == 0x09)
            {
                FlushText();
                sb.Append((char)b);
                i++;
                continue;
            }

            if (b >= 0x20)
            {
                textBuf.Add(b);
                i++;
                continue;
            }

            FlushText();

            switch (b)
            {
                case 0x1B: // ESC
                    i = SkipEscCommand(data, i, ref currentCp);
                    break;
                case 0x1D: // GS
                    i = SkipGsCommand(data, i);
                    break;
                case 0x10: // DLE — length depends on sub-command; default 2 bytes for unknowns
                    i = SkipDleCommand(data, i);
                    break;
                case 0x1C: // FS
                    i = SkipFsCommand(data, i);
                    break;
                case 0x0C: // FF — form feed; treat as newline
                    sb.Append('\n');
                    i++;
                    break;
                default:
                    // Unknown control byte — skip silently
                    i++;
                    break;
            }
        }
        FlushText();
        return TrimBlankLines(sb.ToString());
    }

    private static string TrimBlankLines(string text)
    {
        // Trim leading blank lines
        int start = 0;
        while (start < text.Length)
        {
            int lineEnd = text.IndexOf('\n', start);
            int sliceEnd = lineEnd < 0 ? text.Length : lineEnd;
            if (!string.IsNullOrWhiteSpace(text.Substring(start, sliceEnd - start))) break;
            if (lineEnd < 0) return string.Empty;
            start = lineEnd + 1;
        }

        // Trim trailing blank lines
        int end = text.Length;
        while (end > start)
        {
            int lineStart = text.LastIndexOf('\n', end - 1);
            int sliceStart = lineStart < 0 ? start : lineStart + 1;
            if (!string.IsNullOrWhiteSpace(text.Substring(sliceStart, end - sliceStart))) break;
            end = lineStart < 0 ? start : lineStart;
        }

        return text.Substring(start, end - start);
    }

    // ESC commands: parameter counts based on Epson ESC/POS spec
    private static int SkipEscCommand(byte[] data, int pos, ref int currentCp)
    {
        if (pos + 1 >= data.Length) return data.Length;
        var cmd = data[pos + 1];
        int paramLen = cmd switch
        {
            0x40 => 0,                              // ESC @ initialize
            0x21 or 0x2D or 0x32 or 0x33 or 0x45 or
            0x47 or 0x48 or 0x49 or 0x4A or 0x4D or
            0x4E or 0x4F or 0x52 or 0x53 or 0x54 or
            0x55 or 0x56 or 0x57 or 0x61 or 0x62 or
            0x63 or 0x64 or 0x69 or 0x6D or 0x70 or
            0x71 or 0x72 or 0x73 or 0x76 or 0x7B => 1,
            0x24 or 0x2A or 0x33 or 0x44 => 2,      // some 2-param
            0x5C => 2,                              // ESC \ relative position (2)
            0x74 => 1,                              // ESC t code page (handled specially below)
            _ => 1,                                  // default: conservative 1
        };

        // Special: ESC t n switches the code page
        if (cmd == 0x74 && pos + 2 < data.Length)
        {
            var n = data[pos + 2];
            if (EscCodePageMap.TryGetValue(n, out var cp))
            {
                currentCp = cp;
            }
        }

        // ESC p (drawer pulse) actually takes 3 params
        if (cmd == 0x70) paramLen = 3;
        // ESC c 3/4/5 take 1 param each, total = 2 trailing bytes after ESC
        if (cmd == 0x63) paramLen = 2;
        // ESC * (bit image): 0x2A — 3 bytes header (m, nL, nH) + nL+nH*256 data
        if (cmd == 0x2A && pos + 4 < data.Length)
        {
            int nL = data[pos + 3];
            int nH = data[pos + 4];
            int dataLen = nL + nH * 256;
            paramLen = 3 + dataLen;
        }

        return Math.Min(pos + 2 + paramLen, data.Length);
    }

    // GS commands
    private static int SkipGsCommand(byte[] data, int pos)
    {
        if (pos + 1 >= data.Length) return data.Length;
        var cmd = data[pos + 1];

        // GS V (cut) — handled by listener as job boundary, but if we see it, skip it cleanly
        if (cmd == 0x56)
        {
            if (pos + 2 >= data.Length) return data.Length;
            var m = data[pos + 2];
            int total = m is 65 or 66 or 97 or 98 ? 4 : 3;
            return Math.Min(pos + total, data.Length);
        }

        // GS * x y data — define downloaded bit image (Simphony's logo upload path)
        if (cmd == 0x2A)
        {
            if (pos + 3 >= data.Length) return data.Length;
            int xCount = data[pos + 2];
            int yCount = data[pos + 3];
            int payload = xCount * yCount * 8;
            return Math.Min(pos + 4 + payload, data.Length);
        }

        // GS / m — print downloaded bit image
        if (cmd == 0x2F)
        {
            return Math.Min(pos + 3, data.Length);
        }

        // GS v 0 (raster bit image): 0x76 0x30 m xL xH yL yH d1..dN
        if (cmd == 0x76)
        {
            if (pos + 7 >= data.Length) return data.Length;
            // assume sub-cmd 0x30 (raster)
            int xL = data[pos + 4], xH = data[pos + 5];
            int yL = data[pos + 6], yH = data[pos + 7];
            int xBytes = xL + xH * 256;
            int yLines = yL + yH * 256;
            int payload = xBytes * yLines;
            return Math.Min(pos + 8 + payload, data.Length);
        }

        // GS ( L: 1D 28 4C pL pH ... — variable; skip safely
        if (cmd == 0x28 && pos + 4 < data.Length)
        {
            int pL = data[pos + 3];
            int pH = data[pos + 4];
            int payload = pL + pH * 256;
            return Math.Min(pos + 5 + payload, data.Length);
        }

        int paramLen = cmd switch
        {
            0x21 or 0x42 or 0x45 or 0x46 or 0x48 or
            0x49 or 0x61 or 0x62 or 0x66 or 0x68 or
            0x72 or 0x77 => 1,
            0x4C or 0x57 => 2,                       // GS L, GS W
            0x50 => 2,                               // GS P
            _ => 1,
        };
        return Math.Min(pos + 2 + paramLen, data.Length);
    }

    private static int SkipDleCommand(byte[] data, int pos)
    {
        if (pos + 1 >= data.Length) return data.Length;
        var sub = data[pos + 1];
        int total = sub switch
        {
            0x04 or 0x05 => 3,   // DLE EOT n, DLE ENQ n  (status queries)
            0x14 => 5,           // DLE DC4 m a b (clear / generic) — conservative
            _ => 2,              // unknown DLE — assume 2 bytes (DLE + cmd), no params
        };
        return Math.Min(pos + total, data.Length);
    }

    private static int SkipFsCommand(byte[] data, int pos)
    {
        if (pos + 1 >= data.Length) return data.Length;
        var cmd = data[pos + 1];
        int paramLen = cmd switch
        {
            0x21 or 0x26 or 0x2D or 0x2E or 0x43 or
            0x53 or 0x57 or 0x70 or 0x71 or 0x72 => 1,
            _ => 1,
        };
        if (cmd == 0x70) paramLen = 2;               // FS p m n
        return Math.Min(pos + 2 + paramLen, data.Length);
    }
}
