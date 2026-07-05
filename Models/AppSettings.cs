using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using MunerisIpPrinter.Infrastructure;

namespace MunerisIpPrinter.Models;

public sealed class PrinterConfig : INotifyPropertyChanged
{
    private string _name = "Printer";
    private string _address = "127.0.0.1";

    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; OnChanged(nameof(Name)); } }
    }

    /// <summary>Loopback address — derived from list position (127.0.0.1, .2, …). Not persisted.</summary>
    public string Address
    {
        get => _address;
        set { if (_address != value) { _address = value; OnChanged(nameof(Address)); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));

    public static string AddressForIndex(int index) => $"127.0.0.{index + 1}";
}

public sealed class AppSettings
{
    public List<PrinterConfig> Printers { get; set; } = new();

    /// <summary>When true, the app writes raw req/resp dumps and a debug.log into a "logs" folder next to the exe.</summary>
    public bool LoggingEnabled { get; set; }

    /// <summary>
    /// How many recent receipts to keep (in memory and persisted in the .bin) per printer.
    /// 0 disables history entirely — nothing is loaded, capped, or saved.
    /// </summary>
    public int HistoryCount { get; set; } = DefaultHistoryCount;

    /// <summary>Width of the left sidebar in DIPs. Persisted across runs and clamped on load.</summary>
    public double SidebarWidth { get; set; } = DefaultSidebarWidth;

    /// <summary>Last main-window size in DIPs. Persisted on close and clamped on load.</summary>
    public double WindowWidth { get; set; } = DefaultWindowWidth;
    public double WindowHeight { get; set; } = DefaultWindowHeight;

    public const int DefaultHistoryCount = 0;
    public const int MaxHistoryCount = 200;
    public const int MaxPrinters = 15;
    public const double DefaultSidebarWidth = 200;
    public const double MinSidebarWidth = 140;
    public const double MaxSidebarWidth = 480;
    public const double DefaultWindowWidth = 600;
    public const double DefaultWindowHeight = 765;
    public const double MinWindowWidth = 500;
    public const double MinWindowHeight = 400;

    // Settings live alongside logos and receipt history in MunerisIpPrinter.bin.
    // Slot 0 doesn't collide with logo slots (1-255 = address octets) or history
    // slots (1000-1255). Format is versioned so future fields can be appended safely.
    private const int SettingsSlot = 0;
    private const byte FormatVersion = 1;

    /// <summary>Per-user app data root, e.g. <c>C:\Users\&lt;user&gt;\AppData\Local\MunerisIpPrinter</c>.
    /// All persistent state (settings, logo cache, receipt history, optional logs) lives here so
    /// the .exe folder stays a single portable file.</summary>
    public static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MunerisIpPrinter");

    public static readonly string StorePath = Path.Combine(AppDataDir, "MunerisIpPrinter.bin");

    static AppSettings()
    {
        TryMigrateLegacyStore();
    }

    /// <summary>One-shot migration: if the .bin is still sitting next to the .exe from an older
    /// build, move it into <see cref="AppDataDir"/>. Best-effort — if the move fails the user just
    /// starts with a fresh store.</summary>
    private static void TryMigrateLegacyStore()
    {
        try
        {
            if (File.Exists(StorePath)) return;
            var legacy = Path.Combine(AppContext.BaseDirectory, "MunerisIpPrinter.bin");
            if (string.Equals(legacy, StorePath, StringComparison.OrdinalIgnoreCase)) return;
            if (!File.Exists(legacy)) return;
            Directory.CreateDirectory(AppDataDir);
            File.Move(legacy, StorePath);
        }
        catch { /* best-effort migration */ }
    }

    /// <summary>
    /// Loads settings from slot 0 of MunerisIpPrinter.bin. Missing/invalid → default of one printer.
    /// Addresses are always derived from list position; never persisted.
    /// </summary>
    public static AppSettings Load()
    {
        var fallback = new AppSettings
        {
            Printers = { new PrinterConfig { Name = "Printer" } }
        };

        try
        {
            var store = new SlotStore(StorePath);
            var blob = store.Read(SettingsSlot);
            if (blob != null && blob.Length > 0)
            {
                var loaded = FromBytes(blob);
                if (loaded != null && loaded.Printers.Count > 0)
                {
                    loaded.AssignAddresses();
                    loaded.HistoryCount = ClampHistoryCount(loaded.HistoryCount);
                    loaded.SidebarWidth = ClampSidebarWidth(loaded.SidebarWidth);
                    loaded.WindowWidth = ClampWindowDim(loaded.WindowWidth, DefaultWindowWidth, MinWindowWidth);
                    loaded.WindowHeight = ClampWindowDim(loaded.WindowHeight, DefaultWindowHeight, MinWindowHeight);
                    return loaded;
                }
            }
        }
        catch
        {
            // Corrupt slot or IO error — fall through to the safe default.
        }

        fallback.AssignAddresses();
        return fallback;
    }

    /// <summary>Re-derives each printer's address from its position: 127.0.0.1, .2, …</summary>
    public void AssignAddresses()
    {
        for (int i = 0; i < Printers.Count; i++)
            Printers[i].Address = PrinterConfig.AddressForIndex(i);
    }

    /// <summary>0 = disabled; anything above <see cref="MaxHistoryCount"/> is capped; negatives → 0.</summary>
    public static int ClampHistoryCount(int value)
        => value < 0 ? 0 : (value > MaxHistoryCount ? MaxHistoryCount : value);

    /// <summary>Bounds the persisted sidebar width; falls back to the default if NaN/zero.</summary>
    public static double ClampSidebarWidth(double value)
    {
        if (double.IsNaN(value) || value <= 0) return DefaultSidebarWidth;
        if (value < MinSidebarWidth) return MinSidebarWidth;
        if (value > MaxSidebarWidth) return MaxSidebarWidth;
        return value;
    }

    /// <summary>Floors a window dimension at <paramref name="min"/>; falls back to <paramref name="fallback"/> on NaN/zero.</summary>
    public static double ClampWindowDim(double value, double fallback, double min)
        => double.IsNaN(value) || value <= 0 ? fallback : Math.Max(value, min);

    public void Save()
    {
        var store = new SlotStore(StorePath);
        store.Write(SettingsSlot, ToBytes());
    }

    private byte[] ToBytes()
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write(FormatVersion);
            bw.Write(Printers.Count);
            foreach (var p in Printers)
                bw.Write(p.Name ?? string.Empty); // BinaryWriter prefixes a 7-bit-encoded length
            bw.Write(LoggingEnabled);
            bw.Write(HistoryCount);
            bw.Write(SidebarWidth);
            bw.Write(WindowWidth);
            bw.Write(WindowHeight);
        }
        return ms.ToArray();
    }

    private static AppSettings? FromBytes(byte[] blob)
    {
        try
        {
            using var ms = new MemoryStream(blob);
            using var br = new BinaryReader(ms);

            byte version = br.ReadByte();
            if (version != FormatVersion) return null; // unknown layout — ignore

            int count = br.ReadInt32();
            if (count < 0 || count > MaxPrinters * 4) return null; // sanity check

            var s = new AppSettings { Printers = new List<PrinterConfig>(count) };
            for (int i = 0; i < count; i++)
                s.Printers.Add(new PrinterConfig { Name = br.ReadString() });

            s.LoggingEnabled = br.ReadBoolean();
            s.HistoryCount = br.ReadInt32();
            s.SidebarWidth = br.ReadDouble();
            s.WindowWidth = br.ReadDouble();
            s.WindowHeight = br.ReadDouble();
            return s;
        }
        catch
        {
            return null;
        }
    }
}
