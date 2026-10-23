using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Utility.Server.DropBoxSync;
using Utility.Server.ML.Data;
using Utility.Server.ML.Services;
using Utility.Server.Options;

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
        services.AddOptions<ServerOptions>()
            .Bind(configuration.GetSection(ServerOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString),
                      "Server:ConnectionString must be configured.")
            .Validate(o => o.CommandTimeoutSeconds > 0,
                      "Server:CommandTimeoutSeconds must be positive.")
            .ValidateOnStart();

        // MlDbContext and the services that use it are TRANSIENT, so every page (and every
        // operation) gets its own context instance. Scoped+Dispose was racy: navigating away
        // disposed the scope while a query was still in flight ("Cannot access a disposed
        // context instance"). Transient instances are tracked by the root provider and
        // released at app shutdown, and SqlClient pools the connections anyway.
        services.AddDbContext<MlDbContext>((sp, db) =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ServerOptions>>().Value;

            db.UseSqlServer(opts.ConnectionString, sql =>
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

        services.AddUtilityDropBoxSync(configuration);

        return services;
    }

    /// <summary>
    /// Registers the Dropbox-to-SQL sync: the one writable context in the solution and
    /// the service that fills it.
    /// <para>
    /// Binds <see cref="ServerOptions"/> itself rather than assuming
    /// <see cref="AddUtilityServer"/> ran first. Depending on another registration to
    /// supply your options is a silent failure: without the bind, ConnectionString stays
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
        services.AddOptions<ServerOptions>()
            .Bind(configuration.GetSection(ServerOptions.SectionName));

        services.AddDbContextFactory<DropBoxDbContext>((sp, db) =>
        {
            var opts = sp.GetRequiredService<IOptions<ServerOptions>>().Value;

            db.UseSqlServer(opts.ConnectionString, sql =>
            {
                // A long walk commits in batches, so give each command room but keep a
                // ceiling: a hung sweep should surface as an error, not a hang.
                sql.CommandTimeout(Math.Max(opts.CommandTimeoutSeconds, 120));

                if (opts.EnableRetryOnFailure)
                {
                    sql.EnableRetryOnFailure(maxRetryCount: 5,
                                             maxRetryDelay: TimeSpan.FromSeconds(10),
                                             errorNumbersToAdd: null);
                }
            });
        }, ServiceLifetime.Singleton);

        services.AddSingleton<IDropBoxSyncService, DropBoxSyncService>();

        return services;
    }

    /// <summary>
    /// Connection-string overload for hosts that do not use Microsoft.Extensions.Configuration.
    /// </summary>
    public static IServiceCollection AddUtilityServer(
        this IServiceCollection services,
        string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{ServerOptions.SectionName}:ConnectionString"] = connectionString
            })
            .Build();

        return services.AddUtilityServer(configuration);
    }
}