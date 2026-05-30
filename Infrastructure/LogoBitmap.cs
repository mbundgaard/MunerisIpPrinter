using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MunerisIpPrinter.Infrastructure;

public static class LogoBitmap
{
    /// <summary>
    /// Decodes a slot blob — [x: 1 byte][y: 1 byte][bitmap: x*y*8 bytes] — into a bitmap.
    /// Returns null if the blob is missing or malformed.
    /// </summary>
    public static BitmapSource? FromSlotBytes(byte[]? slot)
    {
        if (slot is null || slot.Length < 2) return null;

        byte x = slot[0];
        byte y = slot[1];
        int payloadLen = x * y * 8;
        if (slot.Length != 2 + payloadLen) return null;

        return Decode(x, y, slot, 2, payloadLen);
    }

    /// <summary>Decodes <paramref name="length"/> bytes of GS * column-major data starting at
    /// <paramref name="offset"/> in <paramref name="payload"/>. byte[]+offset+length stays compatible
    /// with .NET Framework 4.6.2 (no ReadOnlySpan in the BCL).</summary>
    public static BitmapSource Decode(byte x, byte y, byte[] payload, int offset, int length)
    {
        int width = x * 8;
        int height = y * 8;
        var pixels = new byte[width * height];
        // Default: white (255). ESC/POS bit set = printed dot = black (0).
        for (int i = 0; i < pixels.Length; i++) pixels[i] = 255;

        // GS * data is column-major: for each of (8x) columns, y bytes of 8 vertical dots each (MSB top).
        for (int col = 0; col < width; col++)
        {
            int colBase = col * y;
            for (int rowByte = 0; rowByte < y; rowByte++)
            {
                byte b = payload[offset + colBase + rowByte];
                if (b == 0) continue;
                int rowTop = rowByte * 8;
                for (int bit = 0; bit < 8; bit++)
                {
                    if (((b >> (7 - bit)) & 1) != 0)
                    {
                        pixels[(rowTop + bit) * width + col] = 0;
                    }
                }
            }
        }

        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
    }

    public static bool StreamReferencesLogo(byte[] data)
    {
        for (int i = 0; i < data.Length - 1; i++)
        {
            if (data[i] == 0x1D && data[i + 1] == 0x2F) return true;
        }
        return false;
    }
}
