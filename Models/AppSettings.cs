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

/// <summary>A selectable code page: the .NET code page number plus a human label for the dropdown.</summary>
public sealed class CodePageOption
{
    public CodePageOption(int code, string label)
    {
        Code = code;
        Label = label;
    }

    public int Code { get; }
    public string Label { get; }

    // The Settings ComboBox uses a custom control template, where DisplayMemberPath does not
    // reliably drive the selection-box text — falling back to ToString keeps the label showing.
    public override string ToString() => Label;
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

    /// <summary>The .NET code page a printer decodes text with when the stream does not select one
    /// (no <c>ESC t</c>). Emulates a physical printer's configured default (its VMSM/memory-switch
    /// code page). An <c>ESC t n</c> in the data still overrides this per receipt. Default 437 (USA).</summary>
    public int DefaultCodePage { get; set; } = DefaultCodePageValue;

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
    public const int DefaultCodePageValue = 437;

    /// <summary>Code pages offered in the Settings dropdown, in display order. Values are .NET code
    /// page numbers (net462 ships every one of these in-box). The Cyrillic trio (866/1251/855) mirrors
    /// what a BIXOLON SRP-S300 supports via <c>ESC t</c> 17/28/36.</summary>
    public static readonly CodePageOption[] CodePages =
    {
        new(437,  "PC437 — USA / Standard"),
        new(850,  "PC850 — Multilingual (Latin-1)"),
        new(852,  "PC852 — Latin-2 (Central Europe)"),
        new(858,  "PC858 — Multilingual + Euro"),
        new(1252, "WPC1252 — Windows Latin-1"),
        new(866,  "PC866 — Cyrillic (DOS / IBM866)"),
        new(1251, "WPC1251 — Cyrillic (Windows-1251)"),
        new(855,  "PC855 — Cyrillic (DOS)"),
        new(737,  "PC737 — Greek"),
        new(857,  "PC857 — Turkish"),
    };

    // Settings live alongside logos and receipt history in MunerisIpPrinter.bin.
    // Slot 0 doesn't collide with logo slots (1-255 = address octets) or history
    // slots (1000-1255). Format is versioned so future fields can be appended safely.
    private const int SettingsSlot = 0;
    // v1 -> v2 appended DefaultCodePage. v1 blobs still load (code page defaults to 437).
    private const byte FormatVersion = 2;

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
                    loaded.DefaultCodePage = ClampCodePage(loaded.DefaultCodePage);
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

    /// <summary>Falls back to 437 if the persisted code page isn't one the dropdown offers, so a
    /// stale/corrupt value can never leave decoding pointed at an unsupported page.</summary>
    public static int ClampCodePage(int value)
    {
        foreach (var cp in CodePages)
            if (cp.Code == value) return value;
        return DefaultCodePageValue;
    }

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
            bw.Write(DefaultCodePage); // v2
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
            if (version != 1 && version != 2) return null; // unknown layout — ignore

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
            if (version >= 2) s.DefaultCodePage = br.ReadInt32();
            return s;
        }
        catch
        {
            return null;
        }
    }
}
