using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Utility.Server.DropBoxSync;
using Utility.Server.Finder.Repair;
using Utility.Server.Finder.Services;
using Utility.Server.ML.Data;
using Utility.Server.ML.Services;
using Utility.Server.Options;
using Utility.Server.Services;

namespace Utility.Server;

public static class ServerServiceCollectionExtensions
{
    /// <summary>
    /// Registers read-only access to an existing SQL Server database.
    /// <para>
    /// There is no writable context and no migrations: the app reads tables another system
    /// owns, and refuses to emit DDL. <see cref="MlDbContext"/> also throws on SaveChanges.
    /// </para>
    /// </summary>
    public static IServiceCollection AddUtilityServer(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // The SQL settings live in the registry (HKCU\Software\BbcUtility\Sql), not in a
        // configuration file. The registry REPLACES the file as the source: a value that is
        // not in the registry keeps the default on ServerOptions.
        var registry = new SqlRegistrySettings();
        services.TryAddSingleton<ISqlRegistrySettings>(registry);

        services.AddOptions<ServerOptions>()
            .Configure(o =>
            {
                registry.TryApplyTo(o);
            })
            .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString),
                      "Server:ConnectionString must be configured - set it in the registry at " +
                      $"{registry.RegistryKeyPath}.")
            .Validate(o => o.CommandTimeoutSeconds > 0,
                      "Server:CommandTimeoutSeconds must be positive.")
            .Validate(o => o.ConnectTimeoutSeconds > 0,
                      "Server:ConnectTimeoutSeconds must be positive.")
            .ValidateOnStart();

        // MlDbContext and the services that use it are TRANSIENT, so every page (and every
        // operation) gets its own context instance. Scoped+Dispose was racy: navigating away
        // disposed the scope while a query was still in flight ("Cannot access a disposed
        // context instance"). Transient instances are tracked by the root provider and
        // released at app shutdown, and SqlClient pools the connections anyway.
        services.AddDbContext<MlDbContext>((sp, db) =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ServerOptions>>().Value;

            db.UseSqlServer(WithConnectTimeout(opts), sql =>
            {
                sql.CommandTimeout(opts.CommandTimeoutSeconds);

                if (opts.EnableRetryOnFailure)
                {
                    sql.EnableRetryOnFailure(maxRetryCount: 5,
                                             maxRetryDelay: TimeSpan.FromSeconds(10),
                                             errorNumbersToAdd: null);
                }
            });
        }, ServiceLifetime.Transient);

        services.AddTransient<IMlDataService, MlDataService>();
        services.AddTransient<IDatabaseProbe, DatabaseProbe>();

        // How many things the app may do at once: the machine's logical processor count,
        // read once and kept. The Finder's file pass uses it as the size of its worker pool.
        services.AddSingleton<IParallelism, Parallelism>();

        // The Finder: one service that gathers the pairs and computes their verdicts. The
        // library service reads the media library on disk; the preview client fetches a
        // candidate's image from Dropbox and discards it. The scan state is a SINGLETON so
        // the Finder tab and Liem's List share one pass.
        services.AddTransient<IFinderService, FinderService>();
        services.AddSingleton<IFinderScanState, FinderScanState>();
        services.AddSingleton<IFinderLibraryService, FinderLibraryService>();
        services.AddTransient<IDropBoxPreviewClient, DropBoxPreviewClient>();

        // Liem's List: the report of the pairs the verdict leaves unresolved. It reads the
        // rows the pass gathered - it runs no pass of its own.
        services.AddTransient<ILiemListReportService, LiemListReportService>();

        // The REPAIR: what restores the library. It writes files only - never the
        // database, never Dropbox. The log goes through a provider, so where the records
        // go can change without the repair changing.
        services.AddTransient<IFinderRepairService, FinderRepairService>();

        // The log goes in a folder named for the date the recovery started. The factory says
        // so explicitly: the provider has a second constructor that takes the folder, and the
        // container must not be left to choose between them.
        services.AddSingleton<IFinderRepairLogProvider>(sp =>
            new TextFinderRepairLogProvider(
                sp.GetRequiredService<ILogger<TextFinderRepairLogProvider>>()));

        services.AddUtilityDropBoxSync(configuration);

        return services;
    }

    /// <summary>
    /// Registers the Dropbox-to-SQL sync: the one writable context in the solution and
    /// the service that fills it.
    /// <para>
    /// Binds <see cref="ServerOptions"/> itself rather than assuming
    /// <see cref="AddUtilityServer"/> ran first. Depending on another registration to
    /// supply your options is a silent failure: without the values, ConnectionString stays
    /// empty and the first query fails with "The ConnectionString property has not been
    /// initialized", which points nowhere near the real cause.
    /// </para>
    /// <para>
    /// Uses <c>AddDbContextFactory</c> because the sync opens a context per pass and
    /// writes in batches, rather than injecting one scoped instance.
    /// </para>
    /// </summary>
    public static IServiceCollection AddUtilityDropBoxSync(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // The Server options are read from the registry here too, so this registration
        // stands on its own. The values are the same ones AddUtilityServer applied, so
        // applying them twice changes nothing.
        services.AddOptions<ServerOptions>()
            .Configure(o =>
            {
                new SqlRegistrySettings().TryApplyTo(o);
            });

        services.AddDbContextFactory<DropBoxDbContext>((sp, db) =>
        {
            var opts = sp.GetRequiredService<IOptions<ServerOptions>>().Value;

            db.UseSqlServer(WithConnectTimeout(opts), sql =>
            {
                // Same timeout as every other command. The sync used to raise it to a
                // floor of 120s; with one global value there is nothing to raise it to,
                // and two timeouts in one app is how the wrong one ends up being the
                // one that fires.
                sql.CommandTimeout(opts.CommandTimeoutSeconds);

                if (opts.EnableRetryOnFailure)
                {
                    sql.EnableRetryOnFailure(maxRetryCount: 5,
                                             maxRetryDelay: TimeSpan.FromSeconds(10),
                                             errorNumbersToAdd: null);
                }
            });
        }, ServiceLifetime.Singleton);

        // ONE writer at a time for ml.DropBoxNode, and it is SHARED: the walk and the
        // two-phase sync's enrichment pass both write to that table, and two gates
        // would let them deadlock against each other.
        services.AddSingleton<DropBoxWriteGate>();

        services.AddSingleton<IDropBoxSyncService, DropBoxSyncService>();
        services.AddSingleton<IDropBoxSyncTwoPhaseService, DropBoxSyncTwoPhaseService>();
        services.AddSingleton<IDropBoxTreeService, DropBoxTreeService>();

        // The dashboard's figures. A SINGLETON: they are read once at startup and shown
        // on a page that is created fresh every time the user navigates to it, so a
        // transient service would re-query on every navigation.
        services.AddSingleton<IDropBoxSyncStatsService, DropBoxSyncStatsService>();

        return services;
    }

    /// <summary>
    /// Rewrites the configured connection string so it carries a Connect Timeout.
    /// <para>
    /// The connect timeout is SqlClient's, and it is NOT what EF's
    /// <c>CommandTimeout</c> sets. It bounds the whole login handshake - the
    /// pre-login exchange and the server's post-login work - so it is the timeout that
    /// fires when the server is under memory pressure and holding logins open. Left
    /// unset it defaults to 15 seconds, which is short enough to end a walk that was
    /// hours in.
    /// </para>
    /// <para>
    /// Written through <see cref="SqlConnectionStringBuilder"/> rather than appended as
    /// text so an existing Connect Timeout is replaced instead of duplicated, and a
    /// connection string without one gains it.
    /// </para>
    /// </summary>
    private static string WithConnectTimeout(ServerOptions opts)
    {
        var builder = new SqlConnectionStringBuilder(opts.ConnectionString)
        {
            ConnectTimeout = opts.ConnectTimeoutSeconds
        };

        return builder.ConnectionString;
    }

    /// <summary>
    /// Connection-string overload for hosts that do not use Microsoft.Extensions.Configuration.
    /// </summary>
    /// <remarks>
    /// The registry is the source of truth, so the string given here is the FALLBACK: a
    /// host with no registry key still starts, and a host with one uses the registry's
    /// value instead.
    /// </remarks>
    public static IServiceCollection AddUtilityServer(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddOptions<ServerOptions>()
            .Configure(o =>
            {
                if (string.IsNullOrWhiteSpace(o.ConnectionString))
                {
                    o.ConnectionString = connectionString;
                }
            });

        return services.AddUtilityServer(new ConfigurationBuilder().Build());
    }
}