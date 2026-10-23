using System.IO;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Utility.DropBox;
using Utility.Server;
using Utility.UI.Services;
using Utility.UI.ViewModels;
using Utility.UI.Views;

namespace Utility.UI;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _host = BuildHost();
            await _host.StartAsync();

            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Utility failed to start.\n\n{ex.Message}",
                "Utility",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();

        // appsettings.json next to the exe, then machine/user overrides.
        builder.Configuration
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile($"appsettings.{Environment.MachineName}.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables("UTILITY_")
            .AddUserSecrets<App>(optional: true);

        // Register the two libraries against the shared configuration.
        builder.Services.AddUtilityServer(builder.Configuration);
        builder.Services.AddUtilityDropBox(builder.Configuration);

        // UI services.
        builder.Services.AddSingleton<INavigationService, NavigationService>();
        builder.Services.AddSingleton<ISettingsService, SettingsService>();
        builder.Services.AddSingleton<ILogSink, LogSink>();

        // View models and pages are TRANSIENT: each page resolves its own view model,
        // which owns a private MlDbContext. Nothing is shared between pages, and
        // navigation away can never dispose a context another page is still using.
        builder.Services.AddTransient<DashboardViewModel>();
        builder.Services.AddTransient<DropBoxViewModel>();
        builder.Services.AddTransient<MediaViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();

        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddTransient<DashboardView>();
        builder.Services.AddTransient<DropBoxView>();
        builder.Services.AddTransient<MediaView>();
        builder.Services.AddTransient<SettingsView>();

        builder.Logging.AddSimpleConsole();

        return builder.Build();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            try
            {
                await _host.StopAsync(TimeSpan.FromSeconds(5));
                _host.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Shutdown error: {ex.Message}");
            }
        }

        base.OnExit(e);
    }
}