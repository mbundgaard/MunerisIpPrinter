namespace MunerisIpPrinter.Services;

public readonly record struct EscPosEvent(
    bool NeedsMore,
    int Consumed,
    byte[] Reply,
    bool IsCut);

public static class EscPosParser
{
    private static readonly byte[] Empty = Array.Empty<byte>();

    private static EscPosEvent NeedsMore() => new(true, 0, Empty, false);
    private static EscPosEvent Skip(int n) => new(false, n, Empty, false);
    private static EscPosEvent Cut(int n) => new(false, n, Empty, true);
    private static EscPosEvent Respond(int n, byte[] reply) => new(false, n, reply, false);

    // Response bytes derived from observed real-printer behavior (see samples\20260513_214004_001_resp.bin)
    // 0x16 / 0x12 / 0x1E are the "all good, online, paper present" defaults; 0x20 is the printer model ID.

    public static EscPosEvent NextEvent(byte[] data, int pos, int end)
    {
        if (pos >= end) return Skip(0);

        var b = data[pos];

        // Fast path: text or non-command control byte
        if (b != 0x10 && b != 0x1B && b != 0x1D && b != 0x1C)
        {
            return Skip(1);
        }

        if (b == 0x10) return ParseDle(data, pos, end);
        if (b == 0x1D) return ParseGs(data, pos, end);
        if (b == 0x1B) return ParseEsc(data, pos, end);
        return ParseFs(data, pos, end);
    }

    private static EscPosEvent ParseDle(byte[] data, int pos, int end)
    {
        if (pos + 1 >= end) return NeedsMore();
        var sub = data[pos + 1];

        if (sub == 0x04) // DLE EOT n — real-time status
        {
            if (pos + 2 >= end) return NeedsMore();
            var n = data[pos + 2];
            var reply = n switch
            {
                1 => new byte[] { 0x16 },
                2 => new byte[] { 0x12 },
                3 => new byte[] { 0x12 },
                4 => new byte[] { 0x1E },
                _ => new byte[] { 0x12 },
            };
            return Respond(3, reply);
        }

        if (sub == 0x05) // DLE ENQ n — no required reply
        {
            if (pos + 2 >= end) return NeedsMore();
            return Skip(3);
        }

        if (sub == 0x14) // DLE DC4 — variable; best-effort skip 2
        {
            return Skip(2);
        }

        return Skip(2);
    }

    private static EscPosEvent ParseGs(byte[] data, int pos, int end)
    {
        if (pos + 1 >= end) return NeedsMore();
        var sub = data[pos + 1];

        if (sub == 0x56) // GS V — cut (job boundary)
        {
            if (pos + 2 >= end) return NeedsMore();
            var m = data[pos + 2];
            int total = m is 65 or 66 or 97 or 98 ? 4 : 3;
            if (pos + total > end) return NeedsMore();
            return Cut(total);
        }

        if (sub == 0x2A) // GS * x y data — define downloaded bit image (Simphony's logo upload)
        {
            if (pos + 3 >= end) return NeedsMore();
            int xCount = data[pos + 2];
            int yCount = data[pos + 3];
            int total = 4 + xCount * yCount * 8;
            if (pos + total > end) return NeedsMore();
            return Skip(total);
        }

        if (sub == 0x72) // GS r n — paper / drawer status
        {
            if (pos + 2 >= end) return NeedsMore();
            return Respond(3, new byte[] { 0x00 });
        }

        if (sub == 0x49) // GS I n — printer ID
        {
            if (pos + 2 >= end) return NeedsMore();
            var n = data[pos + 2];
            var reply = n switch
            {
                1 or 49 => new byte[] { 0x20 },
                2 or 50 => new byte[] { 0x00 },
                3 or 51 => new byte[] { 0x00 },
                _ => new byte[] { 0x00 },
            };
            return Respond(3, reply);
        }

        if (sub == 0x76) // GS v 0 — raster bit image (only the '0' variant has the documented layout)
        {
            if (pos + 2 >= end) return NeedsMore();
            if (data[pos + 2] != 0x30) return Skip(3);
            if (pos + 7 >= end) return NeedsMore();
            int xBytes = data[pos + 4] + data[pos + 5] * 256;
            int yLines = data[pos + 6] + data[pos + 7] * 256;
            int total = 8 + xBytes * yLines;
            if (pos + total > end) return NeedsMore();
            return Skip(total);
        }

        if (sub == 0x28) // GS ( fn pL pH ...
        {
            if (pos + 4 >= end) return NeedsMore();
            int payload = data[pos + 3] + data[pos + 4] * 256;
            int total = 5 + payload;
            if (pos + total > end) return NeedsMore();
            return Skip(total);
        }

        // Default: 1 parameter byte
        int defLen = 3;
        if (pos + defLen > end) return NeedsMore();
        return Skip(defLen);
    }

    private static EscPosEvent ParseEsc(byte[] data, int pos, int end)
    {
        if (pos + 1 >= end) return NeedsMore();
        var sub = data[pos + 1];

        if (sub == 0x76) // ESC v — transmit paper sensor status
        {
            return Respond(2, new byte[] { 0x00 });
        }

        if (sub == 0x75) // ESC u n — drawer status
        {
            if (pos + 2 >= end) return NeedsMore();
            return Respond(3, new byte[] { 0x00 });
        }

        if (sub == 0x40) return Skip(2);   // ESC @ — init, no params

        if (sub == 0x2A) // ESC * — bit image
        {
            if (pos + 4 >= end) return NeedsMore();
            int dataLen = data[pos + 3] + data[pos + 4] * 256;
            int total = 5 + dataLen;
            if (pos + total > end) return NeedsMore();
            return Skip(total);
        }

        if (sub == 0x70) // ESC p — drawer pulse: m, t1, t2
        {
            int total = 5;
            if (pos + total > end) return NeedsMore();
            return Skip(total);
        }

        // Default: 1 parameter byte
        int defLen = 3;
        if (pos + defLen > end) return NeedsMore();
        return Skip(defLen);
    }

    private static EscPosEvent ParseFs(byte[] data, int pos, int end)
    {
        if (pos + 1 >= end) return NeedsMore();
        var sub = data[pos + 1];

        if (sub == 0x70) // FS p n m — print stored NV bit image
        {
            int total = 4;
            if (pos + total > end) return NeedsMore();
            return Skip(total);
        }

        if (sub == 0x71) // FS q n ... — define NV bit images (variable, n blocks of {xL xH yL yH d...})
        {
            if (pos + 2 >= end) return NeedsMore();
            int n = data[pos + 2];
            int cursor = pos + 3;
            for (int i = 0; i < n; i++)
            {
                if (cursor + 4 > end) return NeedsMore();
                int xBytes = data[cursor] + data[cursor + 1] * 256;
                int yLines = data[cursor + 2] + data[cursor + 3] * 256;
                long block = 4L + (long)xBytes * yLines;
                if (cursor + block > end) return NeedsMore();
                cursor += (int)block;
            }
            return Skip(cursor - pos);
        }

        int defLen = 3;
        if (pos + defLen > end) return NeedsMore();
        return Skip(defLen);
    }
}
