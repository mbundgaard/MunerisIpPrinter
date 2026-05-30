using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MunerisIpPrinter.Models;

public sealed class PrinterConfig : INotifyPropertyChanged
{
    private string _name = "Printer";
    private string _address = "127.0.0.1";

    [JsonPropertyName("name")]
    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; OnChanged(nameof(Name)); } }
    }

    /// <summary>Loopback address — derived from list position (127.0.0.1, .2, …). Not persisted.</summary>
    [JsonIgnore]
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
    [JsonPropertyName("printers")]
    public List<PrinterConfig> Printers { get; set; } = new();

    /// <summary>When true, the app writes raw req/resp dumps and a debug.log into a "logs" folder next to the exe.</summary>
    [JsonPropertyName("logging")]
    public bool LoggingEnabled { get; set; }

    /// <summary>
    /// How many recent receipts to keep (in memory and persisted in the .bin) per printer.
    /// 0 disables history entirely — nothing is loaded, capped, or saved.
    /// </summary>
    [JsonPropertyName("historyCount")]
    public int HistoryCount { get; set; } = DefaultHistoryCount;

    /// <summary>Width of the left sidebar in DIPs. Persisted across runs and clamped on load.</summary>
    [JsonPropertyName("sidebarWidth")]
    public double SidebarWidth { get; set; } = DefaultSidebarWidth;

    /// <summary>Last main-window size in DIPs. Persisted on close and clamped on load.</summary>
    [JsonPropertyName("windowWidth")]
    public double WindowWidth { get; set; } = DefaultWindowWidth;

    [JsonPropertyName("windowHeight")]
    public double WindowHeight { get; set; } = DefaultWindowHeight;

    public const int DefaultHistoryCount = 0;
    public const int MaxHistoryCount = 200;
    public const int MaxPrinters = 15;
    public const double DefaultSidebarWidth = 200;
    public const double MinSidebarWidth = 140;
    public const double MaxSidebarWidth = 480;
    public const double DefaultWindowWidth = 720;
    public const double DefaultWindowHeight = 720;
    public const double MinWindowWidth = 500;
    public const double MinWindowHeight = 400;

    public static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "MunerisIpPrinter.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    /// <summary>
    /// Loads MunerisIpPrinter.json next to the exe. Missing/invalid → default of one printer "Printer".
    /// Addresses are always derived from list position; never persisted. Never creates the file.
    /// </summary>
    public static AppSettings Load()
    {
        var fallback = new AppSettings
        {
            Printers = { new PrinterConfig { Name = "Printer" } }
        };

        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (loaded?.Printers is { Count: > 0 })
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
            // Malformed config — fall through to the safe default.
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
        => Math.Clamp(value, 0, MaxHistoryCount);

    /// <summary>Bounds the persisted sidebar width; falls back to the default if NaN/zero.</summary>
    public static double ClampSidebarWidth(double value)
        => double.IsNaN(value) || value <= 0 ? DefaultSidebarWidth
           : Math.Clamp(value, MinSidebarWidth, MaxSidebarWidth);

    /// <summary>Floors a window dimension at <paramref name="min"/>; falls back to <paramref name="fallback"/> on NaN/zero.
    /// No upper clamp — the user might run on an ultrawide and that's their call.</summary>
    public static double ClampWindowDim(double value, double fallback, double min)
        => double.IsNaN(value) || value <= 0 ? fallback : Math.Max(value, min);

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, JsonOpts);
        File.WriteAllText(FilePath, json);
    }
}
