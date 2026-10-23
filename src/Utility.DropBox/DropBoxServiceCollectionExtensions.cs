using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Utility.DropBox.Options;
using Utility.DropBox.Services;

namespace Utility.DropBox;

public static class DropBoxServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Dropbox side: options, client factory and the file service.
    /// Safe to call when Dropbox is not configured yet — services report
    /// <c>IsConfigured == false</c> instead of throwing at startup.
    /// <para>
    /// Credentials are resolved from the registry
    /// (<c>HKCU\Software\BbcUtility\DropBox</c>) first, then from configuration. The
    /// registry wins because it is the source of truth for this deployment; config-file
    /// values are the fallback for machines with no registry key.
    /// </para>
    /// </summary>
    public static IServiceCollection AddUtilityDropBox(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var registry = new DropBoxRegistrySettings();
        services.TryAddSingleton<IDropBoxRegistrySettings>(registry);

        services.AddOptions<DropBoxOptions>()
            .Bind(configuration.GetSection(DropBoxOptions.SectionName))
            .Configure(o =>
            {
                // Registry overrides configuration-file values when both are present.
                registry.TryApplyTo(o);
            })
            .Validate(o => o.MaxRetriesOnError is >= 0 and <= 10,
                      "DropBox:MaxRetriesOnError must be between 0 and 10.")
            .Validate(o => string.IsNullOrEmpty(o.RootPath) || o.RootPath.StartsWith('/'),
                      "DropBox:RootPath must be an absolute Dropbox path starting with '/'.")
            .ValidateOnStart();

        services.TryAddSingleton<IDropBoxClientFactory, DropBoxClientFactory>();
        services.TryAddSingleton<IDropBoxDataService, DropBoxDataService>();

        return services;
    }

    public static IServiceCollection AddUtilityDropBox(
        this IServiceCollection services,
        string appKey,
        string? refreshToken = null,
        string? accessToken = null)
    {
        var values = new Dictionary<string, string?>
        {
            [$"{DropBoxOptions.SectionName}:AppKey"] = appKey
        };

        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            values[$"{DropBoxOptions.SectionName}:RefreshToken"] = refreshToken;
        }

        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            values[$"{DropBoxOptions.SectionName}:AccessToken"] = accessToken;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        return services.AddUtilityDropBox(configuration);
    }
}