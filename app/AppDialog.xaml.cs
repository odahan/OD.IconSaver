using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace IconSaver;

public partial class AppDialog : Window
{
    private const string DonationUrl = "https://buymeacoffee.com/knhsynths";
    private bool exactMatchesOnly;

    private AppDialog(Window owner, string title, string template)
    {
        InitializeComponent();
        Owner = owner;
        Icon = owner.Icon;
        Title = title;
        DialogContent.ContentTemplate = (DataTemplate)FindResource(template);
    }

    /// <summary>Returns the approved description, or null when the user cancels.</summary>
    internal static string? RequestDescription(Window owner)
    {
        var dialog = new AppDialog(owner, "Save current layout", "DescriptionContent");
        var input = new DescriptionInput();
        dialog.DialogContent.Content = input;
        dialog.AcceptButton.Content = "_Save";
        dialog.AcceptButton.IsDefault = true;
        return dialog.ShowDialog() == true ? input.Description.Trim() : null;
    }

    /// <summary>Asks for explicit approval before removing the selected layout.</summary>
    internal static bool ConfirmDeletion(Window owner, SavedLayout layout)
    {
        var dialog = new AppDialog(owner, "Delete saved layout", "DeleteContent");
        dialog.DialogContent.Content = layout;
        dialog.AcceptButton.Content = "_Delete";
        dialog.FocusCancel();
        return dialog.ShowDialog() == true;
    }

    /// <summary>Returns the chosen match policy, or null when the user cancels the restore.</summary>
    internal static bool? ConfirmLegacyMatches(Window owner, LegacyLabelMatch[] matches)
    {
        var dialog = new AppDialog(owner, "Restore older layout", "LegacyMatchesContent") { Width = 480 };
        dialog.DialogContent.Content = matches;
        dialog.ExactMatchesButton.Visibility = Visibility.Visible;
        dialog.AcceptButton.Content = "Restore by _name";
        dialog.FocusCancel();
        return dialog.ShowDialog() == true ? !dialog.exactMatchesOnly : null;
    }

    /// <summary>Confirms whether to restore the recovery copy or start an empty history.</summary>
    internal static bool ConfirmRecovery(Window owner, bool backupAvailable)
    {
        var dialog = new AppDialog(owner, "Recover layout history", "RecoveryContent");
        dialog.DialogContent.Content = new RecoveryDetails(backupAvailable
            ? "The layout history is unreadable. Recover your saved layouts from the recovery copy?"
            : "The layout history is unreadable and there is no usable recovery copy. Start a new, empty history?");
        dialog.AcceptButton.Content = backupAvailable ? "_Recover layouts" : "_Start new history";
        dialog.FocusCancel();
        return dialog.ShowDialog() == true;
    }

    /// <summary>Shows a screen-related restore refusal or asks for approval of a partial restore.</summary>
    internal static bool ShowScreenRestoreNotice(Window owner, ScreenRestoreNotice notice)
    {
        var dialog = new AppDialog(owner, notice.Title, "ScreenRestoreContent") { Width = 480 };
        dialog.DialogContent.Content = notice;
        if (notice.CanConfirm)
            dialog.AcceptButton.Content = "_Restore active screens";
        else
        {
            dialog.CancelButton.Content = "_Close";
            dialog.AcceptButton.Visibility = Visibility.Collapsed;
        }
        dialog.FocusCancel();
        return dialog.ShowDialog() == true;
    }

    /// <summary>Shows the installed app version and its donation link.</summary>
    internal static void ShowAbout(Window owner)
    {
        string? version = typeof(AppDialog).Assembly.GetName().Version?.ToString(3);
        var dialog = new AppDialog(owner, $"About {owner.Title}", "AboutContent");
        dialog.DialogContent.Content = new AboutDetails(owner.Title, owner.Icon, version);
        dialog.AcceptButton.Visibility = Visibility.Collapsed;
        dialog.CancelButton.Content = "_Close";
        dialog.FocusCancel();
        dialog.ShowDialog();
    }

    private void FocusDescription(object sender, RoutedEventArgs e) => ((UIElement)sender).Focus();

    private void FocusCancel()
    {
        CancelButton.IsDefault = true;
        Loaded += (_, _) => CancelButton.Focus();
    }

    private void CloseWindow(object sender, RoutedEventArgs e) => Close();

    private void Accept(object sender, RoutedEventArgs e) => DialogResult = true;

    private void AcceptExactMatches(object sender, RoutedEventArgs e)
    {
        exactMatchesOnly = true;
        DialogResult = true;
    }

    private void OpenDonationPage(object sender, RoutedEventArgs e)
    {
        try
        {
            using Process? browser = Process.Start(new ProcessStartInfo(DonationUrl) { UseShellExecute = true });
            DonationError.Visibility = Visibility.Collapsed;
        }
        catch (Win32Exception)
        {
            DonationError.Visibility = Visibility.Visible;
        }
    }

    private sealed class DescriptionInput
    {
        public string Description { get; set; } = "";
    }

    private sealed record AboutDetails(string Name, ImageSource Icon, string? Version);

    private sealed record RecoveryDetails(string Message);
}
