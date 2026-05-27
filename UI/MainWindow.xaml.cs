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

    private static readonly Brush TabText = new SolidColorBrush(Color.FromRgb(0xC8, 0xD0, 0xDC));

    private readonly PrintListener _listener;
    private readonly JobLog? _log;
    private readonly List<PrinterView> _views = new();
    private readonly Dictionary<string, PrinterView> _byAddress = new();
    private readonly bool _multiTab;
    private TabControl? _tabs;
    private WebApiServer? _api;

    public MainWindow()
    {
        InitializeComponent();
        TabText.Freeze();

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
            view.SettingsRequested += (_, _) => OpenSettings();
            _views.Add(view);
            _byAddress[cfg.Address] = view;
        }

        _multiTab = _views.Count > 1;
        if (_multiTab)
        {
            _tabs = new TabControl
            {
                Style = (Style)FindResource("DarkTabControlStyle"),
                ItemContainerStyle = (Style)FindResource("DarkTabItemStyle"),
            };
            foreach (var view in _views)
            {
                var header = new TextBlock
                {
                    Text = view.Config.Name,
                    Foreground = TabText,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                };
                _tabs.Items.Add(new TabItem { Header = header, Content = view });
            }
            _tabs.SelectionChanged += OnTabChanged;

            // shown in the content area when no tab is selected (startup, or after ESC)
            var mainPage = new MainPageView();
            mainPage.SettingsRequested += (_, _) => OpenSettings();
            _tabs.Tag = mainPage;

            RootContainer.Children.Add(_tabs);
        }
        else
        {
            RootContainer.Children.Add(_views[0]);
        }

        Title = AppName;
        PreviewKeyDown += OnPreviewKeyDown;

        Loaded += (_, _) =>
        {
            // TabControl auto-selects tab 0; clear it so the app opens with no printer selected.
            if (_tabs != null) _tabs.SelectedIndex = -1;

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

            // /screenshot captures the whole window — only meaningful with a single tab.
            if (!_multiTab)
            {
                _api = new WebApiServer(this, ApiPort);
                try { _api.Start(); } catch { /* port unavailable — non-fatal */ }
            }
        };
        Closed += (_, _) =>
        {
            _listener.Stop();
            _api?.Dispose();
        };
    }

    private void OnJobReceived(object? sender, PrintJob job)
    {
        Dispatcher.Invoke(() =>
        {
            if (_byAddress.TryGetValue(job.LocalAddress, out var view))
            {
                view.AddJob(job);
                _log?.SaveJob(job);

                // flag the tab with a * if the receipt landed on a tab you're not viewing
                if (_tabs != null)
                {
                    int idx = _views.IndexOf(view);
                    if (idx != _tabs.SelectedIndex)
                        MarkTab(idx, hasNew: true);
                }
            }
            else
            {
                // a connection to an unconfigured 127.0.0.x — no tab for it, drop.
                _log?.Line($"dropped job from {job.RemoteEndPoint} — no tab for {job.LocalAddress}");
            }
        });
    }

    private void OnStatusChanged(object? sender, string status)
        => Dispatcher.Invoke(() => _log?.Line(status));

    private void MarkTab(int index, bool hasNew)
        => ((TabItem)_tabs!.Items[index]).Tag = hasNew ? "New" : null;

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged also bubbles up from the receipt list inside each tab — ignore those.
        if (e.Source != _tabs || _tabs!.SelectedIndex < 0) return;
        MarkTab(_tabs.SelectedIndex, hasNew: false);
    }

    private PrinterView ActiveView =>
        _tabs != null && _tabs.SelectedIndex >= 0 ? _views[_tabs.SelectedIndex] : _views[0];

    private void SelectTab(int index)
    {
        if (_tabs != null && index >= 0 && index < _tabs.Items.Count)
            _tabs.SelectedIndex = index;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ActiveView.ClearSelection();           // clear the receipt selection on the active view
            // Deselect via the TabItem (the same path a click uses) — setting SelectedIndex = -1
            // directly leaves the TabItem stuck IsSelected=true, so re-clicking it does nothing.
            if (_tabs?.SelectedItem is TabItem ti)
                ti.IsSelected = false;
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
                ActiveView.CopySelectedReceipt();
                e.Handled = true;
                break;
            case >= Key.D1 and <= Key.D6:
                SelectTab(e.Key - Key.D1);
                e.Handled = true;
                break;
            case >= Key.NumPad1 and <= Key.NumPad6:
                SelectTab(e.Key - Key.NumPad1);
                e.Handled = true;
                break;
        }
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
