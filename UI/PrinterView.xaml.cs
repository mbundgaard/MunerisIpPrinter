using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MunerisIpPrinter.Infrastructure;
using MunerisIpPrinter.Models;
using MunerisIpPrinter.Services;

namespace MunerisIpPrinter.UI;

/// <summary>
/// One printer's detail pane: receipt paper + chip strip. Owns no listener and no toolbar buttons —
/// MainWindow drives <see cref="AddJob"/>, copy/reset, and which view is visible.
/// </summary>
public partial class PrinterView : UserControl
{
    private readonly SlotStore _slotStore;
    private readonly int _slotKey;
    private readonly int _historyCount;
    private readonly ObservableCollection<PrintJob> _jobs = new();
    private int _sequence;

    public PrinterConfig Config { get; }

    /// <summary>Raised whenever the displayed receipt or job list changes — MainWindow uses it to refresh toolbar state.</summary>
    public event EventHandler? StateChanged;

    public PrinterView(PrinterConfig config, SlotStore slotStore, int historyCount)
    {
        InitializeComponent();
        Config = config;
        _slotStore = slotStore;
        _slotKey = IPAddress.Parse(config.Address).GetAddressBytes()[^1];
        _historyCount = historyCount;

        JobList.ItemsSource = _jobs;
        SizePaperTo40Cols();

        if (_historyCount > 0)
        {
            var saved = PrintHistory.Load(_slotStore, _slotKey).Take(_historyCount).ToList();
            if (saved.Count > 0)
            {
                foreach (var job in saved) _jobs.Add(job); // already most-recent-first
                _sequence = saved.Max(j => j.Sequence);
                // nothing selected — the pane opens empty until the user picks a receipt
            }
        }
    }

    /// <summary>True when a receipt is currently rendered on the paper (Copy is meaningful).</summary>
    public bool HasReceiptShown => PaperBorder.Visibility == Visibility.Visible;

    /// <summary>True when this printer has any receipts in its chip list.</summary>
    public bool HasJobs => _jobs.Count > 0;

    /// <summary>Called by MainWindow when a receipt arrives for this printer's address.</summary>
    public void AddJob(PrintJob job)
    {
        job.Sequence = ++_sequence;
        _jobs.Insert(0, job);
        JobList.SelectedIndex = 0;

        // historyCount == 0 disables history: the receipt still shows for the session,
        // but nothing is capped or persisted.
        if (_historyCount > 0)
        {
            while (_jobs.Count > _historyCount)
                _jobs.RemoveAt(_jobs.Count - 1);
            PrintHistory.Save(_slotStore, _slotKey, _jobs.ToList());
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Wipes this printer's chip list and persisted history. Used by the global Reset.</summary>
    public void ClearAllJobs()
    {
        JobList.SelectedIndex = -1;
        _jobs.Clear();
        _sequence = 0;
        if (_historyCount > 0)
            PrintHistory.Save(_slotStore, _slotKey, Array.Empty<PrintJob>());
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Deselects the current receipt and shows the empty pane again.</summary>
    public void ClearSelection() => JobList.SelectedIndex = -1;

    private void SizePaperTo40Cols()
    {
        var typeface = new Typeface(TextView.FontFamily, TextView.FontStyle, TextView.FontWeight, TextView.FontStretch);
        var ft = new FormattedText(
            new string('0', 40),
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, TextView.FontSize, Brushes.Black, 1.0);
        var col40 = ft.Width;
        TextView.MinWidth = col40;
        TextView.MaxWidth = col40;
        LogoView.MaxWidth = col40 * 0.85; // small inset so centering reads visually
    }

    private void JobList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;

        if (JobList.SelectedItem is not PrintJob job)
        {
            PaperBorder.Visibility = Visibility.Collapsed;
            EmptyHint.Visibility = Visibility.Visible;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        PaperBorder.Visibility = Visibility.Visible;
        EmptyHint.Visibility = Visibility.Collapsed;
        StateChanged?.Invoke(this, EventArgs.Empty);

        TextView.Text = EscPosTextExtractor.Extract(job.Data);

        if (LogoBitmap.StreamReferencesLogo(job.Data))
        {
            var bmp = LogoBitmap.FromSlotBytes(_slotStore.Read(_slotKey));
            LogoView.Source = bmp;
            LogoView.Visibility = bmp != null ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            LogoView.Source = null;
            LogoView.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Copies the currently-shown receipt to the clipboard as an image. No-op if no receipt is shown.</summary>
    public async void CopySelectedReceipt()
    {
        if (!HasReceiptShown) return;

        int w = (int)Math.Ceiling(PaperBorder.ActualWidth);
        int h = (int)Math.Ceiling(PaperBorder.ActualHeight);
        if (w <= 0 || h <= 0) return;

        const double scale = 2.0;
        var savedEffect = PaperBorder.Effect;
        PaperBorder.Effect = null; // drop the drop-shadow from the copied image
        PaperBorder.UpdateLayout();

        var rtb = new RenderTargetBitmap(
            (int)(w * scale), (int)(h * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(new VisualBrush(PaperBorder), null, new Rect(0, 0, w, h));
        rtb.Render(dv);
        rtb.Freeze();

        PaperBorder.Effect = savedEffect;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { Clipboard.SetImage(rtb); return; }
            catch { await Task.Delay(60); }
        }
    }
}
