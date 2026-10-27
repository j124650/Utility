using System.Windows;
using Utility.UI.Services;

namespace Utility.UI.Views;

/// <summary>
/// The window the reader is asked in before anything is written (§10.1).
/// </summary>
/// <remarks>
/// <para>
/// A modal window owned by the main window, the same way Liem's List's window is, so the
/// Finder cannot be used while it is up and focus returns to the Finder when it closes.
/// </para>
/// <para>
/// The window is split horizontally: the information above, the details table below. The
/// figures say how big the job is; the table says what the job is.
/// </para>
/// </remarks>
public partial class RecoverConfirmWindow : Window
{
    /// <summary>Whether the reader answered "Recover".</summary>
    public bool Confirmed { get; private set; }

    public RecoverConfirmWindow(
        RecoverConfirmation confirmation,
        RecoverActionList actions)
    {
        InitializeComponent();

        ScopeText.Text = $"Recover {confirmation.Shown:N0} pair(s)?";
        RecoverableText.Text = $"{confirmation.Recoverable:N0} pair(s) will be recovered";

        ParentRestoredText.Text = $"     {confirmation.ParentRestored:N0}  parent restored from Dropbox";
        ChildRegeneratedText.Text = $"     {confirmation.ChildRegenerated:N0}  child regenerated from its parent";
        BothText.Text = $"     {confirmation.Both:N0}  parent restored, then the child regenerated from it";

        DownloadText.Text = $"{confirmation.DownloadDisplay} will be downloaded";
        ReplacedText.Text = $"{confirmation.Replaced:N0} existing file(s) will be replaced";
        FoldersText.Text = $"{confirmation.Folders:N0} folder(s) will be written to {confirmation.LibraryRoot}";

        NotRecoveredText.Text = confirmation.NotRecovered > 0
            ? $"{confirmation.NotRecovered:N0} pair(s) will be left alone - they need a decision, " +
              "or there is nothing to restore them from"
            : string.Empty;

        // The red warning: above a hundred pairs this is not a quick run, and the reader
        // should think about filtering more narrowly first.
        if (confirmation.IsLongRun)
        {
            WarningText.Text =
                $"This will take a long time - {confirmation.Recoverable:N0} pair(s) and {confirmation.DownloadDisplay}.";
            WarningPanel.Visibility = Visibility.Visible;
        }

        // The details table: the actions, in the order they will be performed.
        ActionCaption.Text = actions.Caption;
        ActionsGrid.ItemsSource = actions.Actions;

        // The title states the scope, so the window is identifiable in the task bar and in
        // a screenshot.
        Title = $"Recover Selected - {confirmation.Recoverable:N0} pair(s)";
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }

    private void OnRecover(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }
}
