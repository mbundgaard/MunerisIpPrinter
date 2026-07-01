using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MunerisIpPrinter.Infrastructure;

/// <summary>
/// Byte-mode QR encoder following ISO/IEC 18004. Supports all four ECC levels
/// (L/M/Q/H) and versions 1-40. Auto-picks the smallest version that fits.
/// Hand-rolled — net462 has no in-box QR support and CLAUDE.md forbids adding
/// a NuGet just for this. Focused on the shapes POS printers emit via GS ( k;
/// no numeric/alphanumeric-mode optimisation, no ECI, no structured append.
/// </summary>
public static class QrEncoder
{
    // --- Public API --------------------------------------------------------

    public static BitmapSource Encode(byte[] data, int moduleSize, char eccChar)
    {
        var ecc = EccIndex(eccChar);
        int version = ChooseVersion(data.Length, ecc);
        var modules = BuildMatrix(data, version, ecc);
        return Rasterize(modules, moduleSize < 1 ? 4 : moduleSize);
    }

    // --- Version / capacity ------------------------------------------------

    // Byte-mode data capacity in bytes for [version 1..40] × ecc [L, M, Q, H].
    // From ISO/IEC 18004 Table 7.
    private static readonly int[,] ByteCapacity = new int[40, 4]
    {
        {   17,   14,   11,    7 }, {   32,   26,   20,   14 }, {   53,   42,   32,   24 },
        {   78,   62,   46,   34 }, {  106,   84,   60,   44 }, {  134,  106,   74,   58 },
        {  154,  122,   86,   64 }, {  192,  152,  108,   84 }, {  230,  180,  130,   98 },
        {  271,  213,  151,  119 }, {  321,  251,  177,  137 }, {  367,  287,  203,  155 },
        {  425,  331,  241,  177 }, {  458,  362,  258,  194 }, {  520,  412,  292,  220 },
        {  586,  450,  322,  250 }, {  644,  504,  364,  280 }, {  718,  560,  394,  310 },
        {  792,  624,  442,  338 }, {  858,  666,  482,  382 }, {  929,  711,  509,  403 },
        { 1003,  779,  565,  439 }, { 1091,  857,  611,  461 }, { 1171,  911,  661,  511 },
        { 1273,  997,  715,  535 }, { 1367, 1059,  751,  593 }, { 1465, 1125,  805,  625 },
        { 1528, 1190,  868,  658 }, { 1628, 1264,  908,  698 }, { 1732, 1370,  982,  742 },
        { 1840, 1452, 1030,  790 }, { 1952, 1538, 1112,  842 }, { 2068, 1628, 1168,  898 },
        { 2188, 1722, 1228,  958 }, { 2303, 1809, 1283,  983 }, { 2431, 1911, 1351, 1051 },
        { 2563, 1989, 1423, 1093 }, { 2699, 2099, 1499, 1139 }, { 2809, 2213, 1579, 1219 },
        { 2953, 2331, 1663, 1273 },
    };

    // Total number of data + EC codewords per version (V1..40) (Table 1 / A.1).
    private static readonly int[] TotalCodewords = new int[40]
    {
          26,   44,   70,  100,  134,  172,  196,  242,  292,  346,
         404,  466,  532,  581,  655,  733,  815,  901,  991, 1085,
        1156, 1258, 1364, 1474, 1588, 1706, 1828, 1921, 2051, 2185,
        2323, 2465, 2611, 2761, 2876, 3034, 3196, 3362, 3532, 3706,
    };

    // Number of error-correction codewords per block per [version, ecc].
    private static readonly int[,] EccPerBlock = new int[40, 4]
    {
        {  7, 10, 13, 17 }, { 10, 16, 22, 28 }, { 15, 26, 18, 22 }, { 20, 18, 26, 16 },
        { 26, 24, 18, 22 }, { 18, 16, 24, 28 }, { 20, 18, 18, 26 }, { 24, 22, 22, 26 },
        { 30, 22, 20, 24 }, { 18, 26, 24, 28 }, { 20, 30, 28, 24 }, { 24, 22, 26, 28 },
        { 26, 22, 24, 22 }, { 30, 24, 20, 24 }, { 22, 24, 30, 24 }, { 24, 28, 24, 30 },
        { 28, 28, 28, 28 }, { 30, 26, 28, 28 }, { 28, 26, 26, 26 }, { 28, 26, 30, 28 },
        { 28, 26, 28, 30 }, { 28, 28, 30, 24 }, { 30, 28, 30, 30 }, { 30, 28, 30, 30 },
        { 26, 28, 30, 30 }, { 28, 28, 28, 30 }, { 30, 28, 30, 30 }, { 30, 28, 30, 30 },
        { 30, 28, 30, 30 }, { 30, 28, 30, 30 }, { 30, 28, 30, 30 }, { 30, 28, 30, 30 },
        { 30, 28, 30, 30 }, { 30, 28, 30, 30 }, { 30, 28, 30, 30 }, { 30, 28, 30, 30 },
        { 30, 28, 30, 30 }, { 30, 28, 30, 30 }, { 30, 28, 30, 30 }, { 30, 28, 30, 30 },
    };

    // Number of error-correction blocks per [version, ecc].
    private static readonly int[,] NumBlocks = new int[40, 4]
    {
        {  1,  1,  1,  1 }, {  1,  1,  1,  1 }, {  1,  1,  2,  2 }, {  1,  2,  2,  4 },
        {  1,  2,  4,  4 }, {  2,  4,  4,  4 }, {  2,  4,  6,  5 }, {  2,  4,  6,  6 },
        {  2,  5,  8,  8 }, {  4,  5,  8,  8 }, {  4,  5,  8, 11 }, {  4,  8, 10, 11 },
        {  4,  9, 12, 16 }, {  4,  9, 16, 16 }, {  6, 10, 12, 18 }, {  6, 10, 17, 16 },
        {  6, 11, 16, 19 }, {  6, 13, 18, 21 }, {  7, 14, 21, 25 }, {  8, 16, 20, 25 },
        {  8, 17, 23, 25 }, {  9, 17, 23, 34 }, {  9, 18, 25, 30 }, { 10, 20, 27, 32 },
        { 12, 21, 29, 35 }, { 12, 23, 34, 37 }, { 12, 25, 34, 40 }, { 13, 26, 35, 42 },
        { 14, 28, 38, 45 }, { 15, 29, 40, 48 }, { 16, 31, 43, 51 }, { 17, 33, 45, 54 },
        { 18, 35, 48, 57 }, { 19, 37, 51, 60 }, { 19, 38, 53, 63 }, { 20, 40, 56, 66 },
        { 21, 43, 59, 70 }, { 22, 45, 62, 74 }, { 24, 47, 65, 77 }, { 25, 49, 68, 81 },
    };

    private static int EccIndex(char ecc) => ecc switch
    {
        'L' or 'l' => 0,
        'Q' or 'q' => 2,
        'H' or 'h' => 3,
        _ => 1, // M
    };

    private static int ChooseVersion(int dataLen, int ecc)
    {
        for (int v = 0; v < 40; v++)
            if (ByteCapacity[v, ecc] >= dataLen) return v + 1;
        return 40;
    }

    // --- Matrix construction ----------------------------------------------

    private static bool[,] BuildMatrix(byte[] data, int version, int ecc)
    {
        int size = version * 4 + 17;
        var codewords = BuildCodewords(data, version, ecc);
        var modules = new bool[size, size];
        var reserved = new bool[size, size];

        DrawFunctionPatterns(modules, reserved, version);
        DrawCodewords(modules, reserved, codewords, size);

        // Apply best mask
        int bestMask = 0;
        int bestPenalty = int.MaxValue;
        bool[,] bestModules = modules;
        for (int m = 0; m < 8; m++)
        {
            var candidate = (bool[,])modules.Clone();
            ApplyMask(candidate, reserved, m);
            DrawFormatBits(candidate, ecc, m);
            int penalty = ComputePenalty(candidate, size);
            if (penalty < bestPenalty)
            {
                bestPenalty = penalty;
                bestMask = m;
                bestModules = candidate;
            }
        }

        // Version info for V7+
        if (version >= 7) DrawVersionInfo(bestModules, version);
        return bestModules;
    }

    // Build the interleaved data + ecc codeword stream.
    private static byte[] BuildCodewords(byte[] data, int version, int ecc)
    {
        int total = TotalCodewords[version - 1];
        int numBlocks = NumBlocks[version - 1, ecc];
        int eccPerBlock = EccPerBlock[version - 1, ecc];
        int dataBytes = total - numBlocks * eccPerBlock;
        int shortBlockLen = dataBytes / numBlocks;
        int numShortBlocks = numBlocks - (dataBytes % numBlocks);

        // ---- Build bitstream ----
        var bits = new BitBuffer();
        bits.Append(0b0100, 4); // Byte mode indicator
        int charCountBits = version <= 9 ? 8 : 16;
        bits.Append(data.Length, charCountBits);
        foreach (var b in data) bits.Append(b, 8);

        int capacityBits = dataBytes * 8;
        // Terminator up to 4 bits
        int termLen = System.Math.Min(4, capacityBits - bits.Length);
        if (termLen > 0) bits.Append(0, termLen);
        // Byte align
        while ((bits.Length & 7) != 0) bits.Append(0, 1);
        // Pad bytes 0xEC / 0x11 alternating
        byte[] pad = { 0xEC, 0x11 };
        int padIdx = 0;
        while (bits.Length < capacityBits) { bits.Append(pad[padIdx++ & 1], 8); }

        var dataCodewords = bits.ToBytes();

        // ---- Split into blocks + compute EC ----
        var dataBlocks = new byte[numBlocks][];
        var ecBlocks = new byte[numBlocks][];
        int p = 0;
        for (int i = 0; i < numBlocks; i++)
        {
            int len = shortBlockLen + (i < numShortBlocks ? 0 : 1);
            var block = new byte[len];
            System.Array.Copy(dataCodewords, p, block, 0, len);
            p += len;
            dataBlocks[i] = block;
            ecBlocks[i] = ReedSolomon.Encode(block, eccPerBlock);
        }

        // ---- Interleave ----
        var result = new byte[total];
        int idx = 0;
        int longBlockLen = shortBlockLen + 1;
        for (int col = 0; col < longBlockLen; col++)
        {
            for (int b = 0; b < numBlocks; b++)
                if (col < dataBlocks[b].Length) result[idx++] = dataBlocks[b][col];
        }
        for (int col = 0; col < eccPerBlock; col++)
        {
            for (int b = 0; b < numBlocks; b++)
                result[idx++] = ecBlocks[b][col];
        }
        return result;
    }

    // --- Function patterns ------------------------------------------------

    private static void DrawFunctionPatterns(bool[,] mods, bool[,] rsv, int version)
    {
        int size = mods.GetLength(0);
        // Finder patterns + separators
        DrawFinder(mods, rsv, 0, 0);
        DrawFinder(mods, rsv, size - 7, 0);
        DrawFinder(mods, rsv, 0, size - 7);

        // Timing patterns
        for (int i = 8; i < size - 8; i++)
        {
            mods[i, 6] = (i & 1) == 0;
            rsv[i, 6] = true;
            mods[6, i] = (i & 1) == 0;
            rsv[6, i] = true;
        }

        // Alignment patterns
        var positions = AlignmentPatternPositions(version);
        for (int i = 0; i < positions.Length; i++)
        {
            for (int j = 0; j < positions.Length; j++)
            {
                int r = positions[i], c = positions[j];
                // Skip locations that overlap finder patterns
                bool skip =
                    (r < 7 && c < 7) ||
                    (r < 7 && c > size - 8) ||
                    (r > size - 8 && c < 7);
                if (skip) continue;
                DrawAlignment(mods, rsv, r, c);
            }
        }

        // Reserve format-info region
        for (int i = 0; i < 9; i++) rsv[i, 8] = true;
        for (int i = 0; i < 8; i++) rsv[8, i] = true;
        for (int i = size - 8; i < size; i++) { rsv[i, 8] = true; rsv[8, i] = true; }
        // Dark module
        mods[size - 8, 8] = true;
        rsv[size - 8, 8] = true;

        // Reserve version-info regions for V7+
        if (version >= 7)
        {
            for (int i = 0; i < 6; i++)
                for (int j = 0; j < 3; j++)
                {
                    rsv[size - 11 + j, i] = true;
                    rsv[i, size - 11 + j] = true;
                }
        }
    }

    private static void DrawFinder(bool[,] mods, bool[,] rsv, int x, int y)
    {
        for (int dy = -1; dy <= 7; dy++)
        {
            for (int dx = -1; dx <= 7; dx++)
            {
                int r = y + dy, c = x + dx;
                if (r < 0 || c < 0 || r >= mods.GetLength(0) || c >= mods.GetLength(0)) continue;
                bool on = dx >= 0 && dx <= 6 && dy >= 0 && dy <= 6 &&
                          (dx == 0 || dx == 6 || dy == 0 || dy == 6 ||
                           (dx >= 2 && dx <= 4 && dy >= 2 && dy <= 4));
                mods[c, r] = on;
                rsv[c, r] = true;
            }
        }
    }

    private static void DrawAlignment(bool[,] mods, bool[,] rsv, int r, int c)
    {
        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                bool on = System.Math.Abs(dx) == 2 || System.Math.Abs(dy) == 2 || (dx == 0 && dy == 0);
                mods[c + dx, r + dy] = on;
                rsv[c + dx, r + dy] = true;
            }
        }
    }

    // Alignment pattern centres per version. ISO 18004 Annex E.
    private static int[] AlignmentPatternPositions(int version)
    {
        if (version == 1) return System.Array.Empty<int>();
        int numAlign = version / 7 + 2;
        int step;
        if (version == 32) step = 26;
        else step = (version * 4 + numAlign * 2 + 1) / (numAlign * 2 - 2) * 2;
        var positions = new int[numAlign];
        positions[0] = 6;
        int size = version * 4 + 17;
        for (int i = numAlign - 1, p = size - 7; i > 0; i--, p -= step) positions[i] = p;
        return positions;
    }

    private static void DrawCodewords(bool[,] mods, bool[,] rsv, byte[] cw, int size)
    {
        int bitIdx = 0;
        int total = cw.Length * 8;
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5; // Skip timing column
            for (int vert = 0; vert < size; vert++)
            {
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? size - 1 - vert : vert;
                    if (rsv[x, y]) continue;
                    if (bitIdx < total)
                    {
                        bool bit = ((cw[bitIdx >> 3] >> (7 - (bitIdx & 7))) & 1) != 0;
                        mods[x, y] = bit;
                    }
                    bitIdx++;
                }
            }
        }
    }

    // --- Masks / format / version ----------------------------------------

    private static void ApplyMask(bool[,] mods, bool[,] rsv, int mask)
    {
        int size = mods.GetLength(0);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (rsv[x, y]) continue;
                bool invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    7 => ((x + y) % 2 + x * y % 3) % 2 == 0,
                    _ => false,
                };
                if (invert) mods[x, y] = !mods[x, y];
            }
        }
    }

    private static void DrawFormatBits(bool[,] mods, int ecc, int mask)
    {
        // ECC codes for format info: L=01, M=00, Q=11, H=10.
        int eccBits = ecc switch { 0 => 0b01, 2 => 0b11, 3 => 0b10, _ => 0b00 };
        int data = (eccBits << 3) | mask;
        int rem = data;
        for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        int fmt = ((data << 10) | rem) ^ 0x5412;

        int size = mods.GetLength(0);
        for (int i = 0; i < 15; i++)
        {
            bool bit = ((fmt >> i) & 1) != 0;
            // Top-left
            if (i < 6) mods[8, i] = bit;
            else if (i < 8) mods[8, i + 1] = bit;
            else if (i < 9) mods[7, 8] = bit;
            else mods[14 - i, 8] = bit;
            // Right + bottom
            if (i < 8) mods[size - 1 - i, 8] = bit;
            else mods[8, size - 15 + i] = bit;
        }
    }

    private static void DrawVersionInfo(bool[,] mods, int version)
    {
        int rem = version;
        for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        int info = (version << 12) | rem;
        int size = mods.GetLength(0);
        for (int i = 0; i < 18; i++)
        {
            bool bit = ((info >> i) & 1) != 0;
            int a = i / 3, b = i % 3 + size - 11;
            mods[a, b] = bit;
            mods[b, a] = bit;
        }
    }

    // --- Penalty --------------------------------------------------------

    private static int ComputePenalty(bool[,] mods, int size)
    {
        int penalty = 0;

        // Rule 1: adjacent same-color modules
        for (int y = 0; y < size; y++)
        {
            int runLen = 1;
            for (int x = 1; x < size; x++)
            {
                if (mods[x, y] == mods[x - 1, y]) { runLen++; if (runLen == 5) penalty += 3; else if (runLen > 5) penalty++; }
                else runLen = 1;
            }
        }
        for (int x = 0; x < size; x++)
        {
            int runLen = 1;
            for (int y = 1; y < size; y++)
            {
                if (mods[x, y] == mods[x, y - 1]) { runLen++; if (runLen == 5) penalty += 3; else if (runLen > 5) penalty++; }
                else runLen = 1;
            }
        }

        // Rule 2: 2x2 same-color blocks
        for (int y = 0; y < size - 1; y++)
            for (int x = 0; x < size - 1; x++)
                if (mods[x, y] == mods[x + 1, y] && mods[x, y] == mods[x, y + 1] && mods[x, y] == mods[x + 1, y + 1]) penalty += 3;

        // Rule 3: 1:1:3:1:1 finder-like patterns
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size - 6; x++)
            {
                if (MatchFinder(mods, x, y, true) || MatchFinder(mods, x, y, false)) penalty += 40;
            }
        }
        for (int x = 0; x < size; x++)
        {
            for (int y = 0; y < size - 6; y++)
            {
                if (MatchFinderV(mods, x, y, true) || MatchFinderV(mods, x, y, false)) penalty += 40;
            }
        }

        // Rule 4: dark module balance
        int dark = 0;
        foreach (var v in mods) if (v) dark++;
        int totalMods = size * size;
        int deviation = System.Math.Abs(dark * 20 - totalMods * 10) / totalMods;
        penalty += deviation * 10;

        return penalty;
    }

    private static bool MatchFinder(bool[,] m, int x, int y, bool dark) =>
        m[x, y] == dark && m[x + 1, y] != dark && m[x + 2, y] == dark && m[x + 3, y] == dark && m[x + 4, y] == dark && m[x + 5, y] != dark && m[x + 6, y] == dark;

    private static bool MatchFinderV(bool[,] m, int x, int y, bool dark) =>
        m[x, y] == dark && m[x, y + 1] != dark && m[x, y + 2] == dark && m[x, y + 3] == dark && m[x, y + 4] == dark && m[x, y + 5] != dark && m[x, y + 6] == dark;

    // --- Rasterise ------------------------------------------------------

    private static BitmapSource Rasterize(bool[,] mods, int moduleSize)
    {
        int size = mods.GetLength(0);
        int quiet = 4;
        int totalSize = size + quiet * 2;
        int pixWidth = totalSize * moduleSize;
        int pixHeight = pixWidth;
        var pixels = new byte[pixWidth * pixHeight];
        // Default white
        for (int i = 0; i < pixels.Length; i++) pixels[i] = 255;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (!mods[x, y]) continue;
                int px0 = (x + quiet) * moduleSize;
                int py0 = (y + quiet) * moduleSize;
                for (int dy = 0; dy < moduleSize; dy++)
                    for (int dx = 0; dx < moduleSize; dx++)
                        pixels[(py0 + dy) * pixWidth + (px0 + dx)] = 0;
            }
        }

        var bmp = BitmapSource.Create(pixWidth, pixHeight, 96, 96, PixelFormats.Gray8, null, pixels, pixWidth);
        bmp.Freeze();
        return bmp;
    }

    // --- Helpers -------------------------------------------------------

    private sealed class BitBuffer
    {
        private readonly List<bool> _bits = new();
        public int Length => _bits.Count;
        public void Append(int value, int numBits)
        {
            for (int i = numBits - 1; i >= 0; i--) _bits.Add(((value >> i) & 1) != 0);
        }
        public byte[] ToBytes()
        {
            var buf = new byte[(_bits.Count + 7) / 8];
            for (int i = 0; i < _bits.Count; i++)
                if (_bits[i]) buf[i >> 3] |= (byte)(1 << (7 - (i & 7)));
            return buf;
        }
    }

    private static class ReedSolomon
    {
        // GF(256) with primitive polynomial x^8 + x^4 + x^3 + x^2 + 1 (0x11D).
        private static readonly int[] Exp = new int[512];
        private static readonly int[] Log = new int[256];

        static ReedSolomon()
        {
            int x = 1;
            for (int i = 0; i < 255; i++)
            {
                Exp[i] = x;
                Log[x] = i;
                x <<= 1;
                if (x >= 256) x ^= 0x11D;
            }
            for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
        }

        public static byte[] Encode(byte[] data, int eccLen)
        {
            var gen = GeneratorPoly(eccLen);
            var ec = new byte[eccLen];
            foreach (var d in data)
            {
                int factor = d ^ ec[0];
                for (int i = 0; i < eccLen - 1; i++)
                {
                    ec[i] = (byte)(ec[i + 1] ^ MulGf(gen[eccLen - 1 - i], factor));
                }
                ec[eccLen - 1] = (byte)MulGf(gen[0], factor);
            }
            return ec;
        }

        private static byte[] GeneratorPoly(int degree)
        {
            // g(x) = (x - α^0)(x - α^1)...(x - α^{degree-1})
            var g = new byte[degree + 1];
            g[0] = 1;
            int size = 1;
            for (int i = 0; i < degree; i++)
            {
                var next = new byte[size + 1];
                for (int j = 0; j < size; j++)
                {
                    next[j] ^= (byte)MulGf(g[j], Exp[i]);
                    next[j + 1] ^= g[j];
                }
                g = next;
                size++;
            }
            return g;
        }

        private static int MulGf(int a, int b)
        {
            if (a == 0 || b == 0) return 0;
            return Exp[Log[a] + Log[b]];
        }
    }
}
