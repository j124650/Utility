using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Utility.Server.DropBoxSync;
using Utility.UI.Mvvm;
using Utility.UI.Services;
using Utility.UI.ViewModels;

namespace Utility.UI.Views;

public partial class MainWindow : Window
{
    private readonly INavigationService _navigation;
    private readonly IServiceProvider _services;
    private readonly IAppStatus _status;
    private readonly IDropBoxSyncService _sync;
    private readonly IDropBoxSyncStatsService _stats;
    private readonly FinderViewModel _finder;
    private UserControl? _current;

    /// <summary>
    /// The sync service, exposed so XAML can bind to its live state.
    /// <para>
    /// The busy screen's own DataContext is the service, but the app content BEHIND the
    /// screen needs the service too - it is what decides whether the blur is applied.
    /// </para>
    /// </summary>
    public IDropBoxSyncService Sync => _sync;

    /// <summary>
    /// The Finder's view model, exposed for the same reason the sync service is: the app
    /// content BEHIND the Finder's busy screen needs to know whether the pass is running,
    /// because that is what decides whether the blur is applied.
    /// </summary>
    public FinderViewModel Finder => _finder;

    public MainWindow(
        IServiceProvider services,
        INavigationService navigation,
        IAppStatus status,
        IDropBoxSyncService sync,
        IDropBoxSyncStatsService stats)
    {
        _services = services;
        _navigation = navigation;
        _status = status;
        _sync = sync;
        _stats = stats;
        _finder = services.GetRequiredService<FinderViewModel>();

        InitializeComponent();

        // The status bar binds to the app-wide status, not to whichever page happens to be
        // showing - so navigating never re-reads these facts.
        DataContext = status;

        // The busy screen binds to the SYNC SERVICE itself, not to a copy of its state.
        // The service is a singleton, so the overlay shows the run's progress even if the
        // page that started it has been navigated away from.
        SyncOverlay.DataContext = sync;

        // The FINDER's busy screen binds to the Finder's view model, which is also a
        // singleton (§14.1) - the same pattern as the sync overlay above.
        FinderOverlay.DataContext = _finder;

        // The app opens on the DropBox DB page: it is the index the app is built around,
        // and it reads its figures from the database at startup - no Dropbox call, no
        // long pass.
        NavDashboard.IsChecked = true;
    }

    private void OnNavDashboardClick(object sender, RoutedEventArgs e) => ShowPage(NavigationKeys.Dashboard);

    private void OnNavDropBoxClick(object sender, RoutedEventArgs e) => ShowPage(NavigationKeys.DropBox);

    private void OnNavMediaClick(object sender, RoutedEventArgs e) => ShowPage(NavigationKeys.Media);

    private void OnNavFinderClick(object sender, RoutedEventArgs e) => ShowPage(NavigationKeys.Finder);

    private void OnNavLogsClick(object sender, RoutedEventArgs e) => ShowPage(NavigationKeys.Logs);

    private void OnNavLiemListClick(object sender, RoutedEventArgs e) => ShowPage(NavigationKeys.LiemList);

    private void OnNavSettingsClick(object sender, RoutedEventArgs e) => ShowPage(NavigationKeys.Settings);

    private async void OnStatusRefreshClick(object sender, RoutedEventArgs e)
    {
        IsEnabled = false;
        try
        {
            await _status.LoadAsync();
        }
        finally
        {
            IsEnabled = true;
        }
    }

    /// <summary>
    /// Stops the walk. The rows already written are kept - a sync never deletes anything.
    /// </summary>
    private void OnStopSyncClick(object sender, RoutedEventArgs e) => _sync.Cancel();

    /// <summary>
    /// Dismisses the busy screen and re-reads the index's figures, so the rows the sync
    /// just wrote are counted behind it.
    /// <para>
    /// The tree read that used to sit here took twenty minutes - it loaded every row of
    /// ml.DropBoxNode and rebuilt the hierarchy. The page shows the figures, so the
    /// figures are what is re-read.
    /// </para>
    /// </summary>
    private async void OnCloseSyncClick(object sender, RoutedEventArgs e)
    {
        _sync.AcknowledgeResult();

        try
        {
            await _stats.LoadAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Index stats re-read after sync failed: {ex.Message}");
        }
    }

    private void ShowPage(string key)
    {
        _navigation.NavigateTo(key);

        if (_current is not null)
        {
            PageHost.Children.Remove(_current);
            (_current as IDisposable)?.Dispose();
        }

        _current = (UserControl)_navigation.ResolvePage(key);
        PageHost.Children.Add(_current);

        // Pages load their own data. The dashboard deliberately does NOT reload on
        // navigation: its tree is read once and kept in the singleton tree state, so
        // switching away and back does not re-query the database.
        //
        // The FINDER runs its pass by itself the first time the tab is shown (§14.1), and
        // does not run it again: its view model is a singleton, so the rows it loaded, the
        // filter and the selected row are all still there when the reader comes back.
        switch (_current.DataContext)
        {
            case DropBoxViewModel dropBox:
                _ = dropBox.RefreshAsync();
                break;
            case MediaViewModel media:
                _ = media.SearchAsync();
                break;
            case FinderViewModel finder:
                _ = finder.EnsurePassAsync();
                break;
            case LogsViewModel logs:
                // The logs folder changes while the app runs - a recovery that is going
                // right now is writing into it - so the lists are read when the tab is
                // shown rather than once at startup.
                logs.Refresh();
                break;
            case SettingsViewModel settings:
                _ = settings.LoadAsync();
                break;
        }
    }
}