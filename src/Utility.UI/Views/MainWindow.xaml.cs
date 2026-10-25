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
    private readonly IDropBoxTreeState _treeState;
    private UserControl? _current;

    /// <summary>
    /// The sync service, exposed so XAML can bind to its live state.
    /// <para>
    /// The busy screen's own DataContext is the service, but the app content BEHIND the
    /// screen needs the service too - it is what decides whether the blur is applied.
    /// </para>
    /// </summary>
    public IDropBoxSyncService Sync => _sync;

    public MainWindow(
        IServiceProvider services,
        INavigationService navigation,
        IAppStatus status,
        IDropBoxSyncService sync,
        IDropBoxTreeState treeState)
    {
        _services = services;
        _navigation = navigation;
        _status = status;
        _sync = sync;
        _treeState = treeState;

        InitializeComponent();

        // The status bar binds to the app-wide status, not to whichever page happens to be
        // showing - so navigating never re-reads these facts.
        DataContext = status;

        // The busy screen binds to the SYNC SERVICE itself, not to a copy of its state.
        // The service is a singleton, so the overlay shows the run's progress even if the
        // page that started it has been navigated away from.
        SyncOverlay.DataContext = sync;

        // Setting IsChecked raises Checked, which navigates. The Dashboard tab is hidden,
        // so the app opens on the Finder.
        NavFinder.IsChecked = true;
    }

    private void OnNavDashboardClick(object sender, RoutedEventArgs e) => ShowPage(NavigationKeys.Dashboard);

    private void OnNavDropBoxClick(object sender, RoutedEventArgs e) => ShowPage(NavigationKeys.DropBox);

    private void OnNavMediaClick(object sender, RoutedEventArgs e) => ShowPage(NavigationKeys.Media);

    private void OnNavFinderClick(object sender, RoutedEventArgs e) => ShowPage(NavigationKeys.Finder);

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
    /// Dismisses the busy screen and re-reads the tree, so the rows the sync just wrote
    /// appear behind it.
    /// </summary>
    private async void OnCloseSyncClick(object sender, RoutedEventArgs e)
    {
        _sync.AcknowledgeResult();

        try
        {
            await _treeState.LoadAsync(force: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Tree re-read after sync failed: {ex.Message}");
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
        // switching away and back does not re-query the database. The Finder does not
        // reload either: its pass is long and runs when the user asks for it.
        switch (_current.DataContext)
        {
            case DropBoxViewModel dropBox:
                _ = dropBox.RefreshAsync();
                break;
            case MediaViewModel media:
                _ = media.SearchAsync();
                break;
            case SettingsViewModel settings:
                _ = settings.LoadAsync();
                break;
        }
    }
}