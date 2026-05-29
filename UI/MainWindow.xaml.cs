using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MunerisIpPrinter.Infrastructure;
using MunerisIpPrinter.Models;
using MunerisIpPrinter.Services;

namespace MunerisIpPrinter.UI;

public partial class MainWindow : Window
{
    private const int Port = 9100;
    private const int ApiPort = 9101;

    private static readonly string AppName =
        $"Muneris IP Printer v{Assembly.GetExecutingAssembly().GetName().Version?.Major ?? 1}";

    private static readonly Brush SidebarItemIdle = new SolidColorBrush(Colors.Transparent);
    private static readonly Brush SidebarItemHover = new SolidColorBrush(Color.FromRgb(0x0F, 0x21, 0x38));
    private static readonly Brush SidebarItemSelected = new SolidColorBrush(Color.FromRgb(0x16, 0x29, 0x4A));
    private static readonly Brush SidebarItemSelectedAccent = new SolidColorBrush(Color.FromRgb(0x3A, 0x6F, 0xB8));
    private static readonly Brush SidebarItemText = new SolidColorBrush(Color.FromRgb(0xC8, 0xD0, 0xDC));

    private readonly PrintListener _listener;
    private readonly JobLog? _log;
    private readonly List<PrinterView> _views = new();
    private readonly Dictionary<string, PrinterView> _byAddress = new();
    private readonly Dictionary<PrinterView, SidebarItem> _sidebarItems = new();
    private PrinterView? _active;
    private WebApiServer? _api;

    public MainWindow()
    {
        InitializeComponent();
        SidebarItemIdle.Freeze();
        SidebarItemHover.Freeze();
        SidebarItemSelected.Freeze();
        SidebarItemSelectedAccent.Freeze();
        SidebarItemText.Freeze();

        var settings = AppSettings.Load();
        _log = settings.LoggingEnabled ? new JobLog(AppContext.BaseDirectory) : null;

        var slotStore = new SlotStore(
            Path.Combine(AppContext.BaseDirectory, "MunerisIpPrinter.bin"));

        _listener = new PrintListener(Port, slotStore, _log);
        _listener.JobReceived += OnJobReceived;
        _listener.StatusChanged += OnStatusChanged;

        foreach (var cfg in settings.Printers)
        {
            var view = new PrinterView(cfg, slotStore, settings.HistoryCount);
            view.StateChanged += (_, _) => UpdateToolbar();
            _views.Add(view);
            _byAddress[cfg.Address] = view;

            // every PrinterView stays in the visual tree so its state survives switching
            view.Visibility = Visibility.Collapsed;
            DetailHost.Children.Add(view);

            var item = BuildSidebarItem(view);
            _sidebarItems[view] = item;
            Sidebar.Items.Add(item.Root);
        }

        if (_views.Count > 0) Select(_views[0]);

        Title = AppName;
        PreviewKeyDown += OnPreviewKeyDown;

        Loaded += (_, _) =>
        {
            try
            {
                _listener.Start();
            }
            catch (Exception ex)
            {
                _log?.Line($"port {Port} bind failed: {ex.Message}");
                MessageBox.Show(this,
                    $"Port {Port} is already in use. Close whatever is using it, then start the app again.\n\n{ex.Message}",
                    AppName, MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
                return;
            }

            // /screenshot captures the whole window — meaningful with sidebar + detail.
            _api = new WebApiServer(this, ApiPort);
            try { _api.Start(); } catch { /* port unavailable — non-fatal */ }
        };
        Closed += (_, _) =>
        {
            _listener.Stop();
            _api?.Dispose();
        };
    }

    /// <summary>One row in the sidebar: clickable Border with the printer name and a new-receipt dot.</summary>
    private sealed class SidebarItem
    {
        public required Border Root { get; init; }
        public required Border Accent { get; init; }
        public required System.Windows.Shapes.Ellipse Dot { get; init; }
    }

    private SidebarItem BuildSidebarItem(PrinterView view)
    {
        var accent = new Border
        {
            Width = 3,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = SidebarItemSelectedAccent,
            Visibility = Visibility.Collapsed,
        };

        var name = new TextBlock
        {
            Text = view.Config.Name,
            Foreground = SidebarItemText,
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(12, 0, 8, 0),
        };

        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            Visibility = Visibility.Collapsed,
        };

        var contentGrid = new Grid();
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(name, 0);
        Grid.SetColumn(dot, 1);
        contentGrid.Children.Add(name);
        contentGrid.Children.Add(dot);

        var content = new Grid();
        content.Children.Add(accent);
        content.Children.Add(contentGrid);

        var root = new Border
        {
            Height = 36,
            Margin = new Thickness(0, 1, 0, 1),
            CornerRadius = new CornerRadius(3),
            Background = SidebarItemIdle,
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = content,
        };
        root.MouseEnter += (_, _) => { if (_active != view) root.Background = SidebarItemHover; };
        root.MouseLeave += (_, _) => { if (_active != view) root.Background = SidebarItemIdle; };
        root.PreviewMouseDown += (_, _) => Select(view);

        return new SidebarItem { Root = root, Accent = accent, Dot = dot };
    }

    private void Select(PrinterView view)
    {
        if (_active == view)
        {
            // re-clicking the active printer just clears its new-dot
            if (_sidebarItems.TryGetValue(view, out var same))
                same.Dot.Visibility = Visibility.Collapsed;
            return;
        }

        if (_active != null)
        {
            _active.Visibility = Visibility.Collapsed;
            if (_sidebarItems.TryGetValue(_active, out var prev))
            {
                prev.Root.Background = SidebarItemIdle;
                prev.Accent.Visibility = Visibility.Collapsed;
            }
        }

        _active = view;
        view.Visibility = Visibility.Visible;
        if (_sidebarItems.TryGetValue(view, out var next))
        {
            next.Root.Background = SidebarItemSelected;
            next.Accent.Visibility = Visibility.Visible;
            next.Dot.Visibility = Visibility.Collapsed;
        }
        UpdateToolbar();
    }

    private void UpdateToolbar()
    {
        CopyButton.IsEnabled = _active?.HasReceiptShown == true;
    }

    private void OnJobReceived(object? sender, PrintJob job)
    {
        Dispatcher.Invoke(() =>
        {
            if (_byAddress.TryGetValue(job.LocalAddress, out var view))
            {
                view.AddJob(job);
                _log?.SaveJob(job);

                // mark the sidebar dot if the receipt landed on a printer the user isn't viewing
                if (view != _active && _sidebarItems.TryGetValue(view, out var item))
                    item.Dot.Visibility = Visibility.Visible;
            }
            else
            {
                // a connection to an unconfigured 127.0.0.x — no detail pane for it, drop.
                _log?.Line($"dropped job from {job.RemoteEndPoint} — no view for {job.LocalAddress}");
            }
        });
    }

    private void OnStatusChanged(object? sender, string status)
        => Dispatcher.Invoke(() => _log?.Line(status));

    private void SelectAt(int index)
    {
        if (index < 0 || index >= _views.Count) return;
        Select(_views[index]);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _active?.ClearSelection();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.Control) return;

        switch (e.Key)
        {
            case Key.S:
                OpenSettings();
                e.Handled = true;
                break;
            case Key.C:
                _active?.CopySelectedReceipt();
                e.Handled = true;
                break;
            case >= Key.D1 and <= Key.D9:
                SelectAt(e.Key - Key.D1);
                e.Handled = true;
                break;
            case >= Key.NumPad1 and <= Key.NumPad9:
                SelectAt(e.Key - Key.NumPad1);
                e.Handled = true;
                break;
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void CopyButton_Click(object sender, RoutedEventArgs e)
        => _active?.CopySelectedReceipt();

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_views.All(v => !v.HasJobs)) return;

        int withJobs = _views.Count(v => v.HasJobs);
        var msg = _views.Count > 1
            ? $"Clear receipts from {withJobs} printer{(withJobs == 1 ? "" : "s")}?\nThis cannot be undone."
            : "Clear all receipts?\nThis cannot be undone.";
        var result = MessageBox.Show(this, msg, AppName,
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (result != MessageBoxResult.Yes) return;

        foreach (var v in _views) v.ClearAllJobs();
    }

    private void OpenSettings()
    {
        var dlg = new SettingsWindow { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var choice = MessageBox.Show(this,
            "Settings saved. Restart now to apply?",
            "Muneris IP Printer", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (choice != MessageBoxResult.Yes) return;

        var exe = Environment.ProcessPath;
        if (exe != null)
        {
            // Relaunch after a short delay so this instance fully exits and releases the port first.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c timeout /t 2 /nobreak >nul & start \"\" \"{exe}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        Application.Current.Shutdown();
    }
}
