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
        UpdateAddButton();
    }

    private void OnPrintersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ReassignAddresses();
        UpdateAddButton();
    }

    private void UpdateAddButton()
        => AddButton.IsEnabled = _printers.Count < AppSettings.MaxPrinters;

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
            MessageBox.Show(this, "Add at least one printer.", "Settings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
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
        try
        {
            settings.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save settings:\n{ex.Message}", "Settings",
                MessageBoxButton.OK, MessageBoxImage.Error);
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
