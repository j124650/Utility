using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Utility.UI.Views;

/// <summary>
/// The window the report is read in (docs/liems-list-spec.md §6.7).
/// </summary>
/// <remarks>
/// <para>
/// A modal window of the app — not the reader's browser, and not a tab. It is shown with
/// <see cref="Window.ShowDialog"/> and owned by the main window, so the Finder cannot be
/// used while the report is up, and focus returns to the Finder when it closes. The
/// report is something the reader reads and acts on; it is not a second task.
/// </para>
/// <para>
/// The window carries the standard chrome — a title bar, a close button, and the
/// maximize/restore buttons. It opens maximized, because the report is long and the
/// reader scrolls it, and it can be restored down. It is resizable.
/// </para>
/// </remarks>
public partial class LiemListWindow : Window
{
    private readonly string _file;

    /// <summary>
    /// The folder WebView2 keeps its browser profile in.
    /// </summary>
    /// <remarks>
    /// WebView2 keeps its profile beside the executable unless told otherwise, which
    /// fails when the app is installed under <c>Program Files</c>.
    /// </remarks>
    private static string UserDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Utility",
        "WebView2");

    public LiemListWindow(string file, string title)
    {
        InitializeComponent();

        _file = file;
        Title = title;

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: UserDataFolder);

            // The control is initialised before its source is set.
            await Browser.EnsureCoreWebView2Async(environment);

            Browser.Source = new Uri(_file);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            // The Evergreen runtime is a shared system component. If it is missing, the
            // window says so rather than showing a blank page.
            Fail(
                "The Microsoft Edge WebView2 runtime is not installed on this machine, " +
                "so the report cannot be shown. Install the Evergreen WebView2 runtime " +
                "and try again.");
        }
        catch (Exception ex)
        {
            Fail($"The report could not be opened: {ex.Message}");
        }
    }

    /// <summary>Reports a failure IN the window rather than leaving a blank page.</summary>
    private void Fail(string message)
    {
        FailureMessage.Text = message;
        FailurePanel.Visibility = Visibility.Visible;
        Browser.Visibility = Visibility.Collapsed;
    }
}
