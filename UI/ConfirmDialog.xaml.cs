using System.Windows;

namespace MunerisIpPrinter.UI;

/// <summary>
/// Dark-themed two-button dialog used in place of MessageBox where the native Windows look
/// would clash with the rest of the app. Returns true if the user picks the confirm button.
/// </summary>
public partial class ConfirmDialog : Window
{
    private ConfirmDialog(string title, string message, string confirm, string cancel)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirm;
        CancelButton.Content = cancel;
    }

    public static bool Ask(Window? owner, string title, string message, string confirm = "OK", string cancel = "Cancel")
    {
        var dlg = new ConfirmDialog(title, message, confirm, cancel);
        if (owner != null) dlg.Owner = owner;
        return dlg.ShowDialog() == true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
}
