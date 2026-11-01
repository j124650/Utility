using System.IO;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Utility.DropBox;
using Utility.Server;
using Utility.Server.DropBoxSync;
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
/// Reads the app-wide status facts and the synced Dropbox index's figures once at
/// startup. Both are singletons, so navigating between pages afterwards never
/// re-queries them.
/// <para>
/// The index's figures are AGGREGATES over ml.DropBoxNode - one scan, one row. The
/// tree read that used to sit here loaded every row and rebuilt the hierarchy, which
/// took twenty minutes; the page shows a handful of numbers, so it reads a handful of
/// numbers.
/// </para>
/// </summary>
private static async Task WarmUpAsync(IServiceProvider services)
    {
        try
        {
            await services.GetRequiredService<IAppStatus>().LoadAsync();
            await services.GetRequiredService<IDropBoxSyncStatsService>().LoadAsync();
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

        // The SQL and Dropbox settings live in the Windows registry and are read by the
        // two libraries' own options setup - the registry is the source of truth, and
        // there is no settings file to shadow it. The per-machine JSON and the user
        // secrets are still here for the values the registry does not carry.
        builder.Configuration
            .AddJsonFile($"appsettings.{Environment.MachineName}.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables("UTILITY_")
            .AddUserSecrets<App>(optional: true);

        // The logging levels that used to sit in appsettings.json. The file is gone, so
        // they are set here: EF's command logging is noisy and is not worth keeping, and
        // everything else is Information.
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        // Register the two libraries against the shared configuration.
        builder.Services.AddUtilityServer(builder.Configuration);
        builder.Services.AddUtilityDropBox(builder.Configuration);

        // UI services.
        builder.Services.AddSingleton<INavigationService, NavigationService>();
        builder.Services.AddSingleton<ILogSink, LogSink>();
        builder.Services.AddSingleton<IAppStatus, AppStatus>();
        builder.Services.AddSingleton<IDropBoxTreeState, DropBoxTreeState>();

        // Liem's List: the report is written to a file and shown in the app's own window.
        builder.Services.AddTransient<ILiemListReportPresenter, LiemListReportPresenter>();

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
        builder.Services.AddTransient<SettingsViewModel>();

        // The FINDER's view model is a SINGLETON, and so is Liem's List's. They hold no
        // DbContext - they talk to services - and what they hold is expensive to rebuild:
        // the rows of a pass over the whole library, the filter, the selected row. The
        // Finder tab is loaded once and then looked at (§14.1), so switching tabs and
        // coming back must not throw that away.
        builder.Services.AddSingleton<FinderViewModel>();
        builder.Services.AddSingleton<LiemListViewModel>();

        // The LOGS tab's view model is a singleton too: it holds the tree and the lists
        // the reader has scrolled to, and re-reading the folder every time the tab is
        // opened would lose the reader's place.
        builder.Services.AddSingleton<LogsViewModel>();

        // The two-phase sync's busy screen and its run. A singleton, because the run
        // outlives the page that started it.
        builder.Services.AddSingleton<SyncNewViewModel>();

        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddTransient<DashboardView>();
        builder.Services.AddTransient<DropBoxView>();
        builder.Services.AddTransient<MediaView>();
        builder.Services.AddTransient<FinderView>();
        builder.Services.AddTransient<LiemListView>();
        builder.Services.AddTransient<LogsView>();
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