using System.IO;

namespace MunerisIpPrinter.Infrastructure;

/// <summary>
/// Wraps MunerisIpPrinter.bin as a generic multi-slot blob container, keyed by int.
///
/// File format: a sequence of records, each
///   [key: int32 LE][dataSize: int32 LE][data: dataSize bytes]
///
/// Callers namespace their keys: logos live at the printer's address octet (1, 2, …);
/// receipt history lives at <see cref="PrintHistory"/>'s key range. Each Write rewrites
/// the whole file. Shared across all printers; all access is serialized through the instance lock.
/// </summary>
public sealed class SlotStore
{
    private readonly string _path;
    private readonly object _lock = new();

    public SlotStore(string path) => _path = path;

    public byte[]? Read(int key)
    {
        lock (_lock)
        {
            return LoadAll().TryGetValue(key, out var data) ? data : null;
        }
    }

    public void Write(int key, byte[] slotData)
    {
        lock (_lock)
        {
            var all = LoadAll();
            all[key] = slotData;
            SaveAll(all);
        }
    }

    private Dictionary<int, byte[]> LoadAll()
    {
        var result = new Dictionary<int, byte[]>();
        if (!File.Exists(_path)) return result;

        byte[] bytes;
        try { bytes = File.ReadAllBytes(_path); }
        catch { return result; }

        int i = 0;
        while (i + 8 <= bytes.Length)
        {
            int key = BitConverter.ToInt32(bytes, i);
            int size = BitConverter.ToInt32(bytes, i + 4);
            i += 8;
            if (size < 0 || i + size > bytes.Length) break; // corrupt tail — stop
            var data = new byte[size];
            Array.Copy(bytes, i, data, 0, size);
            result[key] = data;
            i += size;
        }
        return result;
    }

    private void SaveAll(Dictionary<int, byte[]> all)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // BinaryWriter writes int32 LE — same layout as the original Span-based Write calls
        // and compatible with .NET Framework 4.6.2 which lacks FileStream's Span overload.
        using var fs = new FileStream(_path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        foreach (var kv in all)
        {
            bw.Write(kv.Key);
            bw.Write(kv.Value.Length);
            bw.Write(kv.Value);
        }
    }
}
