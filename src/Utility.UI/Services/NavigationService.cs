using System;
using Utility.UI.ViewModels;
using Utility.UI.Views;

namespace Utility.UI.Services;

/// <summary>
/// Lightweight page switching. Views are resolved from DI singletons, so switching
/// pages preserves scroll position and loaded state.
/// </summary>
public interface INavigationService
{
    void NavigateTo(string pageKey);

    string CurrentPage { get; }

    /// <summary>Resolves the view for a page key from the DI container.</summary>
    object ResolvePage(string pageKey);

    event EventHandler? NavigationChanged;
}

public sealed class NavigationService(IServiceProvider services) : INavigationService
{
    public string CurrentPage { get; private set; } = "Dashboard";

    public event EventHandler? NavigationChanged;

    public void NavigateTo(string pageKey)
    {
        CurrentPage = pageKey;
        NavigationChanged?.Invoke(this, EventArgs.Empty);
    }

    public object ResolvePage(string pageKey) => pageKey switch
    {
        "Dashboard" => services.GetService(typeof(DashboardView)) ?? throw new InvalidOperationException("DashboardView not registered."),
        "DropBox"   => services.GetService(typeof(DropBoxView))   ?? throw new InvalidOperationException("DropBoxView not registered."),
        "Media"     => services.GetService(typeof(MediaView))     ?? throw new InvalidOperationException("MediaView not registered."),
        "Settings"  => services.GetService(typeof(SettingsView))  ?? throw new InvalidOperationException("SettingsView not registered."),
        _ => throw new ArgumentOutOfRangeException(nameof(pageKey), pageKey, "Unknown page key.")
    };
}

public static class NavigationKeys
{
    public const string Dashboard = "Dashboard";
    public const string DropBox = "DropBox";
    public const string Media = "Media";
    public const string Settings = "Settings";
}