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

    private const string AppName = "Muneris IP Printer";
    private const string GitHubRepo = "mbundgaard/MunerisIpPrinter";

    private static readonly Version? CurrentVersion =
        Assembly.GetExecutingAssembly().GetName().Version;
    private static readonly string AppVersion =
        CurrentVersion is { } v ? $"v{v}" : "v?";

    private string? _updateUrl;
    private string? _downloadedUpdatePath;
    private CancellationTokenSource? _updatePollCts;

    /// <summary>How often the running app checks GitHub for a newer release. Anything
    /// shorter risks hitting the 60-req/hr anonymous rate limit; longer means a printer
    /// that's been open all day still picks up new builds.</summary>
    private static readonly TimeSpan UpdatePollInterval = TimeSpan.FromHours(4);

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
        SidebarColumn.Width = new GridLength(settings.SidebarWidth);
        Width = settings.WindowWidth;
        Height = settings.WindowHeight;
        // All persistent state lives under %LOCALAPPDATA%\MunerisIpPrinter so the .exe folder
        // stays a single portable file (matches the "drop into Dropbox toolkit" deployment).
        _log = settings.LoggingEnabled ? new JobLog(AppSettings.AppDataDir) : null;
        var slotStore = new SlotStore(AppSettings.StorePath);

        // Bind one socket per configured loopback address (127.0.0.1, .2, …) — keeps Windows
        // Defender Firewall quiet (loopback bypasses it) and isolates per-printer routing at
        // the socket layer rather than inspecting LocalEndPoint at runtime.
        var loopbackAddresses = settings.Printers.Select(p => System.Net.IPAddress.Parse(p.Address));
        _listener = new PrintListener(Port, loopbackAddresses, slotStore, _log);
        _listener.JobReceived += OnJobReceived;
        _listener.StatusChanged += OnStatusChanged;

        foreach (var cfg in settings.Printers)
        {
            var view = new PrinterView(cfg, slotStore, settings.HistoryCount);
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
        VersionLabel.Text = AppVersion;
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
                ConfirmDialog.Show(this,
                    "Port in use",
                    $"Port {Port} is already in use. Close whatever is using it, then start the app again.\n\n{ex.Message}");
                Application.Current.Shutdown();
                return;
            }

            // /screenshot captures the whole window — meaningful with sidebar + detail.
            _api = new WebApiServer(this, ApiPort);
            try { _api.Start(); } catch { /* port unavailable — non-fatal */ }

            // background poll for a newer GitHub release; silent if offline or rate-limited.
            // First check fires immediately, then re-checks every UpdatePollInterval — without
            // this loop a long-running session would always be one restart behind.
            if (CurrentVersion is { } cur)
            {
                _updatePollCts = new CancellationTokenSource();
                _ = PollForUpdatesAsync(cur, _updatePollCts.Token);
            }
        };
        Closed += (_, _) =>
        {
            _listener.Stop();
            _api?.Dispose();
            _updatePollCts?.Cancel();
            PersistSidebarWidth();
        };
    }

    /// <summary>Long-running background loop. Runs <see cref="CheckForUpdatesAsync"/> once
    /// immediately, then again every <see cref="UpdatePollInterval"/> until the window closes.
    /// Skips the call entirely while an update is already downloaded and waiting on a click —
    /// no point hammering GitHub when the user just hasn't restarted yet.</summary>
    private async Task PollForUpdatesAsync(Version current, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (string.IsNullOrEmpty(_downloadedUpdatePath) || !System.IO.File.Exists(_downloadedUpdatePath))
            {
                try { await CheckForUpdatesAsync(current).ConfigureAwait(false); }
                catch { /* update poll must never crash the app */ }
            }
            try { await Task.Delay(UpdatePollInterval, ct).ConfigureAwait(false); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>Three-step update flow: check → background download → sidebar nudge.
    /// While the download is in flight, the sidebar link opens the release page in a browser.
    /// Once the download lands, the link flips to "Update ready!" and clicking it
    /// confirms + applies via UpdateApplier.</summary>
    private async Task CheckForUpdatesAsync(Version current)
    {
        var info = await UpdateChecker.CheckAsync(GitHubRepo, current).ConfigureAwait(false);
        if (info == null) return;
        await ApplyUpdateInfoAsync(info).ConfigureAwait(false);
    }

    /// <summary>Drives the sidebar nudge + background download given a discovered update.
    /// Split out so the periodic poll and the menu's "Check for updates" share the same flow.</summary>
    private async Task ApplyUpdateInfoAsync(UpdateInfo info)
    {
        var versionStr = info.LatestVersion.ToString();

        // Phase 1: just-discovered. The link sends the user to the release page in case
        // they want to see the changelog while the download is still streaming.
        await Dispatcher.InvokeAsync(() =>
        {
            _updateUrl = info.ReleaseUrl;
            UpdateLink.Text = $"v{versionStr} ↗";
            UpdateLink.ToolTip = $"Update available — click to open the release page for v{versionStr}";
            UpdateLink.Visibility = Visibility.Visible;
        });

        if (string.IsNullOrEmpty(info.AssetUrl)) return; // no matching .exe asset — manual update only

        // Phase 2: background download. %TEMP% is per-user and writable everywhere.
        var dlPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"MunerisIpPrinter-update-{versionStr}.exe");
        bool ok = await UpdateApplier.DownloadAsync(info.AssetUrl!, dlPath).ConfigureAwait(false);
        if (!ok) return; // download failed — keep Phase 1 link so the user can still get to the release page

        // Phase 3: ready. Same TextBlock, new wording and new click semantics.
        await Dispatcher.InvokeAsync(() =>
        {
            _downloadedUpdatePath = dlPath;
            UpdateLink.Text = "Update ready!";
            UpdateLink.ToolTip = $"Click to install v{versionStr} and restart";
        });
    }

    /// <summary>Manual "Check for updates" invocation from the hamburger menu.
    /// Triggers the same flow as the periodic poll, but also reports the no-update case
    /// (which the silent poll just skips).</summary>
    private async void CheckForUpdatesMenu_Click(object sender, RoutedEventArgs e)
    {
        // If an update is already downloaded, point the user at the existing sidebar nudge
        // instead of doing redundant work.
        if (!string.IsNullOrEmpty(_downloadedUpdatePath) && System.IO.File.Exists(_downloadedUpdatePath))
        {
            ConfirmDialog.Show(this, "Update ready",
                "An update is already downloaded — click \"Update ready!\" at the bottom of the sidebar to install it.");
            return;
        }

        if (CurrentVersion == null) return;

        var info = await UpdateChecker.CheckAsync(GitHubRepo, CurrentVersion).ConfigureAwait(false);
        if (info == null)
        {
            // null also covers offline/rate-limited; user will know which it is if they
            // just tried this and it failed for real.
            await Dispatcher.InvokeAsync(() =>
                ConfirmDialog.Show(this, "Up to date",
                    $"You're running the latest version (v{CurrentVersion})."));
            return;
        }
        await ApplyUpdateInfoAsync(info).ConfigureAwait(false);
    }

    private void UpdateLink_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // If a fresh build is already downloaded, clicking applies + restarts immediately.
        // The link itself reads "Update ready!" so the action is already self-describing —
        // no need for a separate yes/no confirm.
        if (!string.IsNullOrEmpty(_downloadedUpdatePath) && System.IO.File.Exists(_downloadedUpdatePath))
        {
            var currentExe = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrEmpty(currentExe)) return;
            UpdateApplier.ApplyAndExit(_downloadedUpdatePath!, currentExe!);
            return;
        }

        // Otherwise (download still running or failed), open the release page in a browser.
        if (string.IsNullOrEmpty(_updateUrl)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_updateUrl)
            {
                UseShellExecute = true,
            });
        }
        catch { /* opening the browser is a nicety; never crash the app over it */ }
    }

    /// <summary>Reads back the sidebar column width + window size and writes them to settings.
    /// Swallows IO/state errors — losing layout on a crash is worth less than a hang.</summary>
    private void PersistSidebarWidth()
    {
        try
        {
            var settings = AppSettings.Load();
            var sb = SidebarColumn.ActualWidth;
            if (sb > 0) settings.SidebarWidth = AppSettings.ClampSidebarWidth(sb);
            if (ActualWidth > 0)
                settings.WindowWidth = AppSettings.ClampWindowDim(ActualWidth, AppSettings.DefaultWindowWidth, AppSettings.MinWindowWidth);
            if (ActualHeight > 0)
                settings.WindowHeight = AppSettings.ClampWindowDim(ActualHeight, AppSettings.DefaultWindowHeight, AppSettings.MinWindowHeight);
            settings.Save();
        }
        catch { /* layout persistence is a nicety; never let it block shutdown */ }
    }

    /// <summary>One row in the sidebar: clickable Border with the printer name, name, and an
    /// unviewed-receipt count badge.</summary>
    private sealed class SidebarItem
    {
        public required Border Root { get; init; }
        public required Border Accent { get; init; }
        public required TextBlock Name { get; init; }
        public required Border Badge { get; init; }
        public required TextBlock BadgeText { get; init; }
        public int Unviewed { get; set; }
    }

    private static readonly Brush BadgeFill = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));
    private static readonly Brush NameForegroundAlert = Brushes.White;

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
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var address = new TextBlock
        {
            Text = view.Config.Address,
            Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x6B, 0x7A)),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 10,
            Margin = new Thickness(0, 1, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var badgeText = new TextBlock
        {
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeights.Bold,
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        var badge = new Border
        {
            Background = BadgeFill,
            CornerRadius = new CornerRadius(8),
            MinWidth = 16,
            Height = 16,
            Padding = new Thickness(5, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Visibility = Visibility.Collapsed,
            Child = badgeText,
        };

        var nameEdit = new TextBox
        {
            Style = (Style)FindResource("DarkTextBoxStyle"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 8, 0),
            Padding = new Thickness(2, 1, 2, 1),
            Visibility = Visibility.Collapsed,
        };

        var rename = new Button
        {
            Content = "",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x86, 0x96)),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 3, 6, 3),
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Rename this printer",
            Focusable = false,
            Visibility = Visibility.Collapsed,
        };

        void StartRename()
        {
            nameEdit.Text = name.Text;
            name.Visibility = Visibility.Collapsed;
            nameEdit.Visibility = Visibility.Visible;
            nameEdit.Focus();
            nameEdit.SelectAll();
        }

        void EndRename(bool commit)
        {
            if (nameEdit.Visibility != Visibility.Visible) return;
            if (commit)
            {
                var newName = (nameEdit.Text ?? string.Empty).Trim();
                if (newName.Length > 0 && newName != name.Text)
                {
                    name.Text = newName;
                    view.Config.Name = newName;
                    SavePrinterName(view, newName);
                }
            }
            nameEdit.Visibility = Visibility.Collapsed;
            name.Visibility = Visibility.Visible;
        }

        rename.Click += (_, _) => StartRename();
        nameEdit.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { EndRename(commit: true); e.Handled = true; }
            else if (e.Key == Key.Escape) { EndRename(commit: false); e.Handled = true; }
        };
        nameEdit.LostFocus += (_, _) => EndRename(commit: true);

        // name and nameEdit share the same slot; the address sits underneath both
        var nameSlot = new Grid();
        nameSlot.Children.Add(name);
        nameSlot.Children.Add(nameEdit);

        var labelStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 8, 0),
        };
        labelStack.Children.Add(nameSlot);
        labelStack.Children.Add(address);

        var contentGrid = new Grid();
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // rename sits between name and badge so the badge column anchors to the right edge
        // and doesn't jump when the pencil shows/hides on hover.
        Grid.SetColumn(labelStack, 0);
        Grid.SetColumn(rename, 1);
        Grid.SetColumn(badge, 2);
        contentGrid.Children.Add(labelStack);
        contentGrid.Children.Add(rename);
        contentGrid.Children.Add(badge);

        var content = new Grid();
        content.Children.Add(accent);
        content.Children.Add(contentGrid);

        var root = new Border
        {
            Height = 46,
            Margin = new Thickness(0, 1, 0, 1),
            CornerRadius = new CornerRadius(3),
            Background = SidebarItemIdle,
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = content,
        };
        root.MouseEnter += (_, _) =>
        {
            if (_active != view) root.Background = SidebarItemHover;
            rename.Visibility = Visibility.Visible;
        };
        root.MouseLeave += (_, _) =>
        {
            if (_active != view) root.Background = SidebarItemIdle;
            if (nameEdit.Visibility != Visibility.Visible) rename.Visibility = Visibility.Collapsed;
        };
        root.PreviewMouseDown += (_, e) =>
        {
            // clicks on the pencil or the inline edit box shouldn't also trigger row selection
            if (IsWithin(e.OriginalSource as DependencyObject, rename)) return;
            if (IsWithin(e.OriginalSource as DependencyObject, nameEdit)) return;
            Select(view);
        };

        return new SidebarItem
        {
            Root = root,
            Accent = accent,
            Name = name,
            Badge = badge,
            BadgeText = badgeText,
        };
    }

    /// <summary>Sets the visible badge count and brightens the name when there's at least one unviewed receipt.</summary>
    private static void RefreshBadge(SidebarItem item)
    {
        if (item.Unviewed <= 0)
        {
            item.Badge.Visibility = Visibility.Collapsed;
            item.Name.Foreground = SidebarItemText;
            item.Name.FontWeight = FontWeights.SemiBold;
        }
        else
        {
            item.BadgeText.Text = item.Unviewed > 99 ? "99+" : item.Unviewed.ToString();
            item.Badge.Visibility = Visibility.Visible;
            item.Name.Foreground = NameForegroundAlert;
            item.Name.FontWeight = FontWeights.Bold;
        }
    }

    /// <summary>Writes a printer's new name back to MunerisIpPrinter.json. Positional: the index in
    /// _views matches the index in the persisted Printers list.</summary>
    private void SavePrinterName(PrinterView view, string newName)
    {
        try
        {
            int idx = _views.IndexOf(view);
            if (idx < 0) return;
            var settings = AppSettings.Load();
            if (idx >= settings.Printers.Count) return;
            settings.Printers[idx].Name = newName;
            settings.Save();
        }
        catch { /* renaming is a nicety; never crash on a disk hiccup */ }
    }

    private static bool IsWithin(DependencyObject? source, DependencyObject target)
    {
        for (var n = source; n != null; n = System.Windows.Media.VisualTreeHelper.GetParent(n))
            if (ReferenceEquals(n, target)) return true;
        return false;
    }


    private void Select(PrinterView view)
    {
        if (_active == view)
        {
            // re-clicking the active printer clears its unviewed count
            if (_sidebarItems.TryGetValue(view, out var same))
            {
                same.Unviewed = 0;
                RefreshBadge(same);
            }
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
            next.Unviewed = 0;
            RefreshBadge(next);
        }
    }

    private void OnJobReceived(object? sender, PrintJob job)
    {
        Dispatcher.Invoke(() =>
        {
            if (_byAddress.TryGetValue(job.LocalAddress, out var view))
            {
                view.AddJob(job);
                _log?.SaveJob(job);

                // bump the unviewed badge if the receipt landed somewhere the user isn't looking
                if (view != _active && _sidebarItems.TryGetValue(view, out var item))
                {
                    item.Unviewed++;
                    RefreshBadge(item);
                }
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
            _active?.ScrollToNewest();
            e.Handled = true;
            return;
        }

        // Accept Ctrl with optional Shift, but no Alt/Win — Shift gates Ctrl+C → text-copy variant.
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        if ((Keyboard.Modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return;

        switch (e.Key)
        {
            case Key.S:
                OpenSettings();
                e.Handled = true;
                break;
            case Key.C:
                // Ctrl+Shift+C → newest receipt text, Ctrl+C → newest receipt image
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                    _active?.CopyNewestText();
                else
                    _active?.CopyNewestImage();
                e.Handled = true;
                break;
            case >= Key.D1 and <= Key.D9:
                SelectAt(e.Key - Key.D1);
                e.Handled = true;
                break;
            case Key.D0:
                SelectAt(9); // Ctrl+0 → 10th printer
                e.Handled = true;
                break;
            case >= Key.NumPad1 and <= Key.NumPad9:
                SelectAt(e.Key - Key.NumPad1);
                e.Handled = true;
                break;
            case Key.NumPad0:
                SelectAt(9);
                e.Handled = true;
                break;
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void AboutMenu_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AboutWindow(GitHubRepo) { Owner = this };
        dlg.ShowDialog();
    }

    /// <summary>Pops up the styled sidebar overflow menu (Clear all receipts / Settings) above the button.</summary>
    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.ContextMenu == null)
        {
            var itemStyle = (Style)FindResource("AppMenuItemStyle");

            var clear = new MenuItem
            {
                Header = "Clear all receipts",
                Icon = MenuIcon(""),
                Style = itemStyle,
            };
            clear.Click += ResetButton_Click;

            var settings = new MenuItem
            {
                Header = "Settings",
                Icon = MenuIcon(""),
                Style = itemStyle,
            };
            settings.Click += SettingsButton_Click;

            var check = new MenuItem
            {
                Header = "Check for updates",
                Icon = MenuIcon(""),
                Style = itemStyle,
            };
            check.Click += CheckForUpdatesMenu_Click;

            var about = new MenuItem
            {
                Header = "About",
                Icon = MenuIcon(""),
                Style = itemStyle,
            };
            about.Click += AboutMenu_Click;

            var menu = new ContextMenu { Style = (Style)FindResource("AppContextMenuStyle") };
            menu.Items.Add(clear);
            menu.Items.Add(settings);
            menu.Items.Add(check);
            menu.Items.Add(about);
            btn.ContextMenu = menu;
        }
        btn.ContextMenu.PlacementTarget = btn;
        btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        btn.ContextMenu.IsOpen = true;
    }

    /// <summary>Builds a Segoe MDL2 glyph for use as a MenuItem.Icon.</summary>
    private static TextBlock MenuIcon(string glyph) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily("Segoe MDL2 Assets"),
        FontSize = 14,
        FontWeight = FontWeights.Normal,
        Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xD0, 0xDC)),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_views.All(v => !v.HasJobs)) return;

        int withJobs = _views.Count(v => v.HasJobs);
        var msg = _views.Count > 1
            ? $"This will clear receipts from {withJobs} printer{(withJobs == 1 ? "" : "s")}. This cannot be undone."
            : "This will clear all stored receipts. This cannot be undone.";
        if (!ConfirmDialog.Ask(this,
                title: "Clear receipts?",
                message: msg,
                confirm: "Clear",
                cancel: "Cancel")) return;

        foreach (var v in _views)
        {
            v.ClearAllJobs();
            // The unviewed-count badge lives on the sidebar item, not the PrinterView —
            // ClearAllJobs only wipes the receipts, so reset the badge here too.
            if (_sidebarItems.TryGetValue(v, out var item))
            {
                item.Unviewed = 0;
                RefreshBadge(item);
            }
        }
    }

    private void OpenSettings()
    {
        var dlg = new SettingsWindow { Owner = this };
        if (dlg.ShowDialog() != true) return;

        if (!ConfirmDialog.Ask(this,
                title: "Settings saved",
                message: "Restart the app to apply the new settings? Your changes are already saved either way.",
                confirm: "Restart now",
                cancel: "Later")) return;

        // Environment.ProcessPath is .NET 6+; on net462 we use the entry assembly path.
        var exe = Assembly.GetEntryAssembly()?.Location;
        if (exe != null)
            Relauncher.RelaunchAfterExit(exe);
        Application.Current.Shutdown();
    }
}
