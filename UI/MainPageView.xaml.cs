using System.Windows;
using System.Windows.Controls;

namespace MunerisIpPrinter.UI;

/// <summary>
/// Shown in the TabControl's content area when no printer tab is selected — just the
/// brand logo and a settings gear.
/// </summary>
public partial class MainPageView : UserControl
{
    /// <summary>Raised when the user clicks the gear button.</summary>
    public event EventHandler? SettingsRequested;

    public MainPageView() => InitializeComponent();

    private void Settings_Click(object sender, RoutedEventArgs e)
        => SettingsRequested?.Invoke(this, EventArgs.Empty);
}
