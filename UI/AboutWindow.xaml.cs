using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Documents;

namespace MunerisIpPrinter.UI;

public partial class AboutWindow : Window
{
    public AboutWindow(string githubRepo)
    {
        InitializeComponent();
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionLabel.Text = v != null ? $"v{v}" : "v?";
        GithubLink.NavigateUri = new Uri($"https://github.com/{githubRepo}");
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Hyperlink h || h.NavigateUri == null) return;
        try
        {
            Process.Start(new ProcessStartInfo(h.NavigateUri.AbsoluteUri) { UseShellExecute = true });
        }
        catch { /* opening the browser is a nicety; never crash on it */ }
        e.Handled = true;
    }

}
