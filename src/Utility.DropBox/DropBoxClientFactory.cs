using Dropbox.Api;
using Dropbox.Api.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Utility.DropBox.Options;

namespace Utility.DropBox;

/// <summary>
/// Creates configured <see cref="DropboxClient"/> instances. A factory exists so the
/// token can rotate without the caller holding a stale client.
/// </summary>
public interface IDropBoxClientFactory
{
    /// <summary>True when options carry enough credentials to build a client.</summary>
    bool IsConfigured { get; }

    /// <summary>Builds a client from the current options.</summary>
    DropboxClient CreateClient();

    /// <summary>
    /// Builds a client and verifies it by fetching the current account.
    /// Throws <see cref="DropBoxNotConfiguredException"/> when credentials are missing.
    /// </summary>
    Task<DropboxClient> CreateValidatedClientAsync(CancellationToken ct = default);

    /// <summary>
    /// Builds a client scoped to a specific namespace.
    /// <para>
    /// The Dropbox API resolves paths against the caller's <b>personal</b> space by
    /// default. Business team folders are not mounted there, so they stay invisible
    /// (and return <c>path/not_found</c>) until the client is pointed at the team root
    /// via <see cref="DropboxClient.WithPathRoot"/>. Pass <c>null</c> for the personal
    /// namespace.
    /// </para>
    /// </summary>
    Task<DropboxClient> CreateClientForNamespaceAsync(string? namespaceId, CancellationToken ct = default);
}

public sealed class DropBoxClientFactory(
    IOptions<DropBoxOptions> options,
    ILogger<DropBoxClientFactory> logger) : IDropBoxClientFactory
{
    private readonly DropBoxOptions _options = options.Value;

    public bool IsConfigured => _options.IsConfigured;

    public DropboxClient CreateClient()
    {
        if (string.IsNullOrWhiteSpace(_options.AppKey))
        {
            throw new DropBoxNotConfiguredException(
                "DropBox:AppKey is not configured. Register an app at https://www.dropbox.com/developers/apps.");
        }

        var config = new DropboxClientConfig(
            userAgent: "Utility/0.1.0",
            maxRetriesOnError: _options.MaxRetriesOnError);

        // The SDK auto-refreshes the access token when constructed with a refresh token.
        DropboxClient client;
        if (!string.IsNullOrWhiteSpace(_options.RefreshToken))
        {
            client = string.IsNullOrWhiteSpace(_options.AppSecret)
                ? new DropboxClient(_options.RefreshToken, _options.AppKey, config)
                : new DropboxClient(_options.RefreshToken, _options.AppKey, _options.AppSecret, config);
        }
        else if (!string.IsNullOrWhiteSpace(_options.AccessToken))
        {
            client = new DropboxClient(_options.AccessToken, config);
        }
        else
        {
            throw new DropBoxNotConfiguredException(
                "No Dropbox token available. Set DropBox:RefreshToken (recommended) or DropBox:AccessToken.");
        }

        // Note: this SDK version has no host/sandbox switch. Sandbox apps use a
        // separate App Key issued by the Dropbox App Console, so nothing to do here.
        return client;
    }

    public async Task<DropboxClient> CreateValidatedClientAsync(CancellationToken ct = default)
    {
        var client = CreateClient();

        // Cheap round-trip that also proves the token works and has the right scopes.
        var account = await client.Users.GetCurrentAccountAsync();
        logger.LogInformation($"Dropbox client ready for account {account.Email}.");

        return client;
    }

    public async Task<DropboxClient> CreateClientForNamespaceAsync(string? namespaceId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var client = CreateClient();

        // Personal namespace: no path root, which is the SDK default.
        if (string.IsNullOrWhiteSpace(namespaceId))
        {
            return client;
        }

        // WithPathRoot returns a new client sharing the same token/transport, so this is
        // cheap and safe to call per request.
        var scoped = client.WithPathRoot(new PathRoot.Root(namespaceId));
        logger.LogInformation("Dropbox client scoped to namespace {NamespaceId}.", namespaceId);
        return await Task.FromResult(scoped);
    }
}

public sealed class DropBoxNotConfiguredException(string message) : Exception(message);