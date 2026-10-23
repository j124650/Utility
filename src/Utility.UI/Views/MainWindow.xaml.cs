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
    private UserControl? _current;

    public MainWindow(IServiceProvider services, INavigationService navigation)
    {
        _services = services;
        _navigation = navigation;

        InitializeComponent();

        // Setting IsChecked raises Checked, which navigates.
        NavDashboard.IsChecked = true;
    }

    private void OnNavDashboardClick(object sender, RoutedEventArgs e) => ShowPage(NavigationKeys.Dashboard);

    private void OnNavDropBoxClick(object sender, RoutedEventArgs e) => ShowPage(NavigationKeys.DropBox);

    private void OnNavMediaClick(object sender, RoutedEventArgs e) => ShowPage(NavigationKeys.Media);

    private void OnNavSettingsClick(object sender, RoutedEventArgs e) => ShowPage(NavigationKeys.Settings);

    private void ShowPage(string key)
    {
        _navigation.NavigateTo(key);

        if (_current is not null)
        {
            TrackStatus(null);
            PageHost.Children.Remove(_current);
            (_current as IDisposable)?.Dispose();
        }

        _current = (UserControl)_navigation.ResolvePage(key);
        PageHost.Children.Add(_current);

        // Refresh the page's data every time it is shown.
                var dataContext = _current.DataContext;
                TrackStatus(dataContext);

        switch (dataContext)
        {
            case DashboardViewModel dashboard:
                _ = dashboard.RefreshAsync();
                break;
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

    /// <summary>
    /// Mirrors the current page's StatusMessage into the window status bar as it changes.
    /// Without this the bar keeps the value from navigation time and looks frozen.
    /// </summary>
    private void TrackStatus(object? dataContext)
    {
        if (_tracked is not null)
        {
            _tracked.PropertyChanged -= OnStatusChanged;
            _tracked = null;
        }

        if (dataContext is ViewModelBase vm)
        {
            _tracked = vm;
            vm.PropertyChanged += OnStatusChanged;
            StatusText.Text = vm.HasStatus ? vm.StatusMessage : Title;
        }
        else
        {
            StatusText.Text = Title;
        }
    }

    private ViewModelBase? _tracked;

    private void OnStatusChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ViewModelBase.StatusMessage) || _tracked is null)
        {
            return;
        }

        StatusText.Text = _tracked.HasStatus ? _tracked.StatusMessage : Title;
    }
}