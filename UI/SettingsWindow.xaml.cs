using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using MunerisIpPrinter.Models;

namespace MunerisIpPrinter.UI;

public partial class SettingsWindow : Window
{
    private readonly ObservableCollection<PrinterConfig> _printers;

    public SettingsWindow()
    {
        InitializeComponent();

        var settings = AppSettings.Load();
        _printers = new ObservableCollection<PrinterConfig>(
            settings.Printers.Select(p => new PrinterConfig { Name = p.Name }));
        _printers.CollectionChanged += OnPrintersChanged;

        ReassignAddresses();
        PrinterList.ItemsSource = _printers;
        LoggingCheck.IsChecked = settings.LoggingEnabled;
        HistoryCountBox.Text = settings.HistoryCount.ToString();

        CodePageCombo.ItemsSource = AppSettings.CodePages;
        int cp = AppSettings.ClampCodePage(settings.DefaultCodePage);
        CodePageCombo.SelectedItem = AppSettings.CodePages.FirstOrDefault(o => o.Code == cp)
                                     ?? AppSettings.CodePages[0];

        NavList.SelectedIndex = 0; // show the Printers section first
        UpdateAddButton();
    }

    private void OnPrintersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ReassignAddresses();
        UpdateAddButton();
    }

    private void UpdateAddButton()
        => AddButton.IsEnabled = _printers.Count < AppSettings.MaxPrinters;

    /// <summary>Left-nav switches which section pane is shown; Save/Cancel act across both.</summary>
    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PrintersPanel == null || GeneralPanel == null) return; // fires once during InitializeComponent
        bool printers = NavList.SelectedIndex == 0;
        PrintersPanel.Visibility = printers ? Visibility.Visible : Visibility.Collapsed;
        GeneralPanel.Visibility = printers ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Addresses are positional: 1st printer is 127.0.0.1, 2nd is .2, … Renumbers after add/remove.</summary>
    private void ReassignAddresses()
    {
        for (int i = 0; i < _printers.Count; i++)
            _printers[i].Address = PrinterConfig.AddressForIndex(i);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
        => _printers.Add(new PrinterConfig { Name = "Printer" });

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PrinterConfig cfg })
            _printers.Remove(cfg);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_printers.Count == 0)
        {
            ConfirmDialog.Show(this, "Settings",
                "Add at least one printer before saving.");
            return;
        }

        foreach (var p in _printers)
            if (string.IsNullOrWhiteSpace(p.Name)) p.Name = "Printer";

        int historyCount = int.TryParse(HistoryCountBox.Text, out var n)
            ? AppSettings.ClampHistoryCount(n)
            : AppSettings.DefaultHistoryCount;

        // Load-then-mutate so fields the dialog doesn't touch (sidebar width, future additions) survive.
        var settings = AppSettings.Load();
        settings.Printers = _printers.ToList();
        settings.LoggingEnabled = LoggingCheck.IsChecked == true;
        settings.HistoryCount = historyCount;
        settings.DefaultCodePage = (CodePageCombo.SelectedItem as CodePageOption)?.Code
                                   ?? AppSettings.DefaultCodePageValue;
        try
        {
            settings.Save();
        }
        catch (Exception ex)
        {
            ConfirmDialog.Show(this, "Settings",
                $"Could not save settings: {ex.Message}");
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
