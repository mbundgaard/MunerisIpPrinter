using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using MunerisIpPrinter.Infrastructure;
using MunerisIpPrinter.Models;
using MunerisIpPrinter.Services;

namespace MunerisIpPrinter.UI;

/// <summary>
/// One printer's detail pane: every receipt stacked newest-on-top inside a scroll view.
/// MainWindow feeds receipts in via <see cref="AddJob"/>; copy is per-receipt (hover-revealed).
/// </summary>
public partial class PrinterView : UserControl
{
    private static readonly Brush ReceiptText = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
    private static readonly Brush MutedText = new SolidColorBrush(Color.FromRgb(0x60, 0x6B, 0x7A));
    private static readonly Brush IconIdle = new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB2));
    private static readonly DropShadowEffect PaperShadow = new()
    {
        ShadowDepth = 3, BlurRadius = 22, Opacity = 0.7, Color = Colors.Black,
    };

    static PrinterView()
    {
        ReceiptText.Freeze();
        MutedText.Freeze();
        IconIdle.Freeze();
        PaperShadow.Freeze();
    }

    private readonly SlotStore _slotStore;
    private readonly int _slotKey;
    private readonly int _historyCount;
    private readonly ObservableCollection<PrintJob> _jobs = new();
    private int _sequence;
    private double _paperWidth;
    private double _logoMaxWidth;

    // A RichTextBox forcibly rewrites its hosted FlowDocument's PagePadding to {5,0,5,0} on
    // measure, regardless of what we set on the document. That 10px of horizontal inset shrinks
    // the usable text region, wrapping the last ~2 chars of a full 40-char line. We add it back
    // to the box width so the text area itself equals _paperWidth (40 chars).
    private const double RichTextHostPadding = 10;

    public PrinterConfig Config { get; }

    public PrinterView(PrinterConfig config, SlotStore slotStore, int historyCount)
    {
        InitializeComponent();
        Config = config;
        _slotStore = slotStore;
        var bytes = IPAddress.Parse(config.Address).GetAddressBytes();
        _slotKey = bytes[bytes.Length - 1];
        _historyCount = historyCount;

        ComputePaperDimensions();

        if (_historyCount > 0)
        {
            var saved = PrintHistory.Load(_slotStore, _slotKey).Take(_historyCount).ToList();
            if (saved.Count > 0)
            {
                foreach (var job in saved)
                {
                    _jobs.Add(job); // already most-recent-first
                    ReceiptStack.Children.Add(BuildReceiptVisual(job));
                }
                _sequence = saved.Max(j => j.Sequence);
            }
        }

        RefreshEmptyState();
    }

    /// <summary>True when this printer has any receipts in its stack.</summary>
    public bool HasJobs => _jobs.Count > 0;

    /// <summary>Called by MainWindow when a receipt arrives for this printer's address.</summary>
    public void AddJob(PrintJob job)
    {
        job.Sequence = ++_sequence;
        _jobs.Insert(0, job);
        var visual = BuildReceiptVisual(job);
        ReceiptStack.Children.Insert(0, visual);
        AnimateArrival(visual);

        // historyCount == 0 disables persistence: the receipt still shows for the session,
        // but nothing is capped or saved across runs.
        if (_historyCount > 0)
        {
            while (_jobs.Count > _historyCount)
            {
                _jobs.RemoveAt(_jobs.Count - 1);
                ReceiptStack.Children.RemoveAt(ReceiptStack.Children.Count - 1);
            }
            PrintHistory.Save(_slotStore, _slotKey, _jobs.ToList());
        }

        // newest just appeared at index 0 — scroll the view back to the top so the user sees it
        ReceiptScroll.ScrollToTop();
        RefreshEmptyState();
    }

    /// <summary>Slides the new receipt into view from above with a quick fade.
    /// Cheap visual cue that something just landed — no glow, no shadow tricks.</summary>
    private static void AnimateArrival(UIElement visual)
    {
        var translate = new TranslateTransform { Y = -24 };
        visual.RenderTransform = translate;
        visual.Opacity = 0;

        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(220);

        translate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation { From = -24, To = 0, Duration = duration, EasingFunction = ease });
        visual.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation { From = 0, To = 1, Duration = duration, EasingFunction = ease });
    }

    /// <summary>Wipes this printer's chip list and persisted history.
    /// The global Reset calls this on every PrinterView.</summary>
    public void ClearAllJobs()
    {
        _jobs.Clear();
        ReceiptStack.Children.Clear();
        _sequence = 0;
        if (_historyCount > 0)
            PrintHistory.Save(_slotStore, _slotKey, Array.Empty<PrintJob>());
        RefreshEmptyState();
    }

    /// <summary>Scrolls the receipts view to the top (newest receipt). Bound to ESC in MainWindow.</summary>
    public void ScrollToNewest() => ReceiptScroll.ScrollToTop();

    /// <summary>Copies the newest receipt's decoded text to the clipboard. Bound to Ctrl+Shift+C.</summary>
    public void CopyNewestText()
    {
        if (_jobs.Count == 0) return;
        try { Clipboard.SetText(EscPosTextExtractor.Extract(_jobs[0].Data)); }
        catch { /* clipboard transient errors are fine to swallow */ }
    }

    /// <summary>Copies the newest receipt as an image. Bound to Ctrl+C.</summary>
    public void CopyNewestImage()
    {
        if (ReceiptStack.Children.Count == 0) return;
        // each stack child is a wrapper StackPanel; the paper Border is its last child
        if (ReceiptStack.Children[0] is not StackPanel wrapper) return;
        var paper = FindPaper(wrapper);
        if (paper == null) return;
        CopyBorderAsImage(paper);
    }

    private static Border? FindPaper(StackPanel wrapper)
    {
        for (int i = wrapper.Children.Count - 1; i >= 0; i--)
            if (wrapper.Children[i] is Border b && b.Background == Brushes.White) return b;
        return null;
    }

    private void ComputePaperDimensions()
    {
        var typeface = new Typeface(new FontFamily("Consolas"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        // Measure in Display formatting mode to match the RichTextBox (TextOptions.SetTextFormattingMode
        // = Display below), so 40 chars measure at the same per-glyph advance the box renders them with.
        var ft = new FormattedText(
            new string('0', 40),
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, 11, Brushes.Black, null, TextFormattingMode.Display, 1.0);
        _paperWidth = ft.Width;
        _logoMaxWidth = _paperWidth * 0.85;
    }

    private void RefreshEmptyState()
    {
        bool any = _jobs.Count > 0;
        ReceiptScroll.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Builds one receipt's stacked visual: small header (seq · time), hover-revealed
    /// copy strip, then the white paper.</summary>
    private UIElement BuildReceiptVisual(PrintJob job)
    {
        var headerText = job.ReceivedAt.ToString("HH:mm:ss");
        var header = new TextBlock
        {
            Text = headerText,
            Foreground = MutedText,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 4),
        };

        // hover-revealed copy buttons, right-aligned, sit in the same row as the header
        var copyText = MakeIconButton("", "Copy receipt text");
        var copyImage = MakeIconButton("", "Copy receipt as image");
        var copyStrip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 0, 4),
            Visibility = Visibility.Hidden,
        };
        copyStrip.Children.Add(copyText);
        copyStrip.Children.Add(copyImage);

        var headerRow = new Grid();
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(header, 0);
        Grid.SetColumn(copyStrip, 1);
        headerRow.Children.Add(header);
        headerRow.Children.Add(copyStrip);

        // EscPosRenderer builds a fully-styled FlowDocument (bold, alignment, size, underline,
        // reverse, inline raster bitmaps, stored logos via GS /, and QR codes via GS ( k). The
        // separate LogoView from the previous design is gone — inline images live in the
        // FlowDocument at the correct paragraph position now.
        var doc = EscPosRenderer.Render(job.Data, _slotStore, _slotKey);

        var richText = new RichTextBox
        {
            Document = doc,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            IsDocumentEnabled = false,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = ReceiptText,
            Padding = new Thickness(0),
            // +host padding so the text region (not the box) equals _paperWidth's 40 chars.
            MinWidth = _paperWidth + RichTextHostPadding,
            MaxWidth = _paperWidth + RichTextHostPadding,
            Focusable = false,
        };
        richText.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        richText.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        TextOptions.SetTextFormattingMode(richText, TextFormattingMode.Display);

        var contentStack = new StackPanel();
        contentStack.Children.Add(richText);

        var paper = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(1),
            Padding = new Thickness(22, 28, 22, 28),
            Effect = PaperShadow,
            Child = contentStack,
        };

        var wrapper = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 24),
            // Transparent (not null) background makes the whole wrapper bounding box
            // hit-testable, so moving from the paper up to the copy strip doesn't cross a
            // dead gap that fires MouseLeave and hides the buttons before they can be clicked.
            Background = Brushes.Transparent,
        };
        wrapper.Children.Add(headerRow);
        wrapper.Children.Add(paper);

        // Plain-text copy still uses EscPosTextExtractor — the FlowDocument is for display only.
        var plainText = EscPosTextExtractor.Extract(job.Data);
        copyText.Click += (_, _) => { try { Clipboard.SetText(plainText); } catch { } };
        copyImage.Click += (_, _) => CopyBorderAsImage(paper);

        wrapper.MouseEnter += (_, _) => copyStrip.Visibility = Visibility.Visible;
        wrapper.MouseLeave += (_, _) => copyStrip.Visibility = Visibility.Hidden;

        return wrapper;
    }

    private static Button MakeIconButton(string glyph, string tooltip) => new()
    {
        Content = glyph,
        FontFamily = new FontFamily("Segoe MDL2 Assets"),
        FontSize = 13,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Foreground = IconIdle,
        Padding = new Thickness(6, 3, 6, 3),
        Cursor = Cursors.Hand,
        ToolTip = tooltip,
        Focusable = false,
    };

    /// <summary>Renders the white receipt paper to a bitmap and publishes it as both
    /// CF_BITMAP and PNG so any paste target (Paint, Word, Slack, browsers) picks a format it knows.</summary>
    private async void CopyBorderAsImage(Border paper)
    {
        var png = RenderBorder(paper, out var rtb);
        if (png == null || rtb == null) return;

        var data = new DataObject();
        data.SetImage(rtb);
        data.SetData("PNG", new MemoryStream(png));

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { Clipboard.SetDataObject(data, copy: true); return; }
            catch { await Task.Delay(60); }
        }
    }

    /// <summary>Renders a receipt paper <see cref="Border"/> to PNG bytes (2×), dropping the
    /// drop-shadow first, and hands back the <paramref name="rtb"/> for CF_BITMAP clipboard use.
    /// UI-thread only. Returns null (and null rtb) when the border has no size yet.
    /// (net462 has no in-box System.ValueTuple, hence the out-param rather than a tuple return.)</summary>
    private static byte[]? RenderBorder(Border paper, out RenderTargetBitmap? rtb)
    {
        rtb = null;
        int w = (int)Math.Ceiling(paper.ActualWidth);
        int h = (int)Math.Ceiling(paper.ActualHeight);
        if (w <= 0 || h <= 0) return null;

        const double scale = 2.0;
        var savedEffect = paper.Effect;
        paper.Effect = null; // drop the drop-shadow from the rendered image
        paper.UpdateLayout();

        rtb = new RenderTargetBitmap(
            (int)(w * scale), (int)(h * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(new VisualBrush(paper), null, new Rect(0, 0, w, h));
        rtb.Render(dv);
        rtb.Freeze();

        paper.Effect = savedEffect;

        var pngStream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        encoder.Save(pngStream);
        return pngStream.ToArray();
    }

    /// <summary>PNG of the newest receipt's paper only (no window chrome), or null if this printer
    /// has no receipts / isn't laid out yet. For the local HTTP API. UI-thread only.</summary>
    public byte[]? RenderNewestReceiptPng()
    {
        if (ReceiptStack.Children.Count == 0) return null;
        if (ReceiptStack.Children[0] is not StackPanel wrapper) return null;
        var paper = FindPaper(wrapper);
        return paper == null ? null : RenderBorder(paper, out _);
    }

    /// <summary>Decoded text of the newest receipt, or null if there are none. UI-thread only.</summary>
    public string? NewestReceiptText()
        => _jobs.Count == 0 ? null : EscPosTextExtractor.Extract(_jobs[0].Data);
}
