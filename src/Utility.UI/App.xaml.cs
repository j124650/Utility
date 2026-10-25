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

                        // Load the slow-changing facts ONCE, after the window is up so the status bar
                        // can paint its pending state first. Fire-and-forget on purpose: a slow or
                        // unreachable database must not block the shell from appearing.
                        _ = WarmUpAsync(_host.Services);
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

    /// <summary>
/// Reads the app-wide status facts and the synced tree once at startup. Both are singletons,
/// so navigating between pages afterwards never re-queries them.
/// </summary>
private static async Task WarmUpAsync(IServiceProvider services)
    {
        try
        {
            await services.GetRequiredService<IAppStatus>().LoadAsync();
            await services.GetRequiredService<IDropBoxTreeState>().LoadAsync();
        }
        catch (Exception ex)
        {
            // Never fatal: the shell stays usable and the status bar shows the failure.
            services.GetRequiredService<ILogSink>().Error($"Startup warm-up failed: {ex.Message}");
        }
    }

    private static IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();

        // The app's OWN log file, opened before the host so the very first log line
        // already lands in it. One file per run, named for the moment the app
        // started: a run's file says which run it belongs to.
        //
        // The file is opened here rather than lazily, because "when the app started"
        // is a fact about THIS process, not about when something first needed
        // logging.
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Utility",
            "logs");

        var fileLog = FileLog.TryOpen(logDirectory, DateTime.Now);
        builder.Logging.AddProvider(new FileLoggerProvider(fileLog));

        // The same file instance, shared with the app's own LogSink, so the two
        // channels (the logging pipeline and the sink) land in one file in order.
        builder.Services.AddSingleton(fileLog);

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
        builder.Services.AddSingleton<IAppStatus, AppStatus>();
        builder.Services.AddSingleton<IDropBoxTreeState, DropBoxTreeState>();

        // The sync service is a singleton (registered in AddUtilityServer): a walk of the
        // team folder takes a long time, and it must survive the user navigating away
        // from the page that started it. It owns and publishes its own run state, so the
        // busy screen binds to it directly - there is no second service copying the
        // progress record out of it.

        // View models and pages are TRANSIENT: each page resolves its own view model,
        // which owns a private MlDbContext. Nothing is shared between pages, and
        // navigation away can never dispose a context another page is still using.
        builder.Services.AddTransient<DashboardViewModel>();
        builder.Services.AddTransient<DropBoxViewModel>();
        builder.Services.AddTransient<MediaViewModel>();
        builder.Services.AddTransient<FinderViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();

        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddTransient<DashboardView>();
        builder.Services.AddTransient<DropBoxView>();
        builder.Services.AddTransient<MediaView>();
        builder.Services.AddTransient<FinderView>();
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