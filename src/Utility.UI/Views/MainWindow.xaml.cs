using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Utility.UI.Mvvm;
using Utility.UI.Services;
using Utility.UI.ViewModels;

namespace Utility.UI.Views;

public partial class MainWindow : Window
{
    private readonly INavigationService _navigation;
    private readonly IServiceProvider _services;
    private readonly IAppStatus _status;
    private UserControl? _current;

    public MainWindow(IServiceProvider services, INavigationService navigation, IAppStatus status)
    {
        _services = services;
        _navigation = navigation;
        _status = status;

        InitializeComponent();

        // The status bar binds to the app-wide status, not to whichever page happens to be
        // showing - so navigating never re-reads these facts.
        DataContext = status;

        // Setting IsChecked raises Checked, which navigates.
        NavDashboard.IsChecked = true;
    }

    private void OnNavDashboardClick(object sender, RoutedEventArgs e) => ShowPage(NavigationKeys.Dashboard);

    private void OnNavDropBoxClick(object sender, RoutedEventArgs e) => ShowPage(NavigationKeys.DropBox);

    private void OnNavMediaClick(object sender, RoutedEventArgs e) => ShowPage(NavigationKeys.Media);

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