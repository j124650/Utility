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

    /// <summary>
    /// One client per namespace, created once and reused.
    /// <para>
    /// The account holds a REFRESH token, and the SDK exchanges it for an access token
    /// lazily - on the first request a client makes. A client used for one request only
    /// therefore costs TWO round trips: the OAuth exchange and the call. Reusing the
    /// client is what stops half the walk's traffic going to the token endpoint.
    /// </para>
    /// </summary>
    private readonly Dictionary<string, DropboxClient> _clients = new(StringComparer.Ordinal);

    private readonly object _clientGate = new();

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

        // The client is CACHED, one per namespace.
        //
        // This is not a micro-optimisation - it halves the HTTP traffic. The account
        // holds a REFRESH token, and the SDK exchanges it for an access token lazily, on
        // the first request a client makes. So a client that is used once costs TWO
        // round trips: the OAuth exchange and the call itself. Verified against the SDK:
        // one client doing 10 requests makes 1 token call and 10 API calls, while ten
        // clients doing 1 request each make 10 token calls and 10 API calls.
        //
        // Building a client per request therefore sent half the walk's traffic to the
        // token endpoint - and each of those round trips holds a request permit, so it
        // also halved the walk's effective parallelism.
        //
        // WithPathRoot returns a client sharing the same token and transport, so the
        // scoped one is cached the same way. The token refresh state lives in the
        // client, and reusing the client is what keeps that state - and the access
        // token - shared.
        if (namespaceId is null || string.IsNullOrWhiteSpace(namespaceId))
        {
            // Personal namespace: no path root, which is the SDK default.
            return GetOrCreate(null);
        }

        return GetOrCreate(namespaceId);
    }

    /// <summary>
    /// The client for a namespace, created once and reused.
    /// </summary>
    private DropboxClient GetOrCreate(string? namespaceId)
    {
        var key = namespaceId ?? string.Empty;

        lock (_clientGate)
        {
            if (_clients.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var client = CreateClient();

            // Personal namespace: no path root, which is the SDK default.
            var scoped = namespaceId is null
                ? client
                : client.WithPathRoot(new PathRoot.Root(namespaceId));

            logger.LogInformation(
                "Dropbox client created for namespace {NamespaceId}; it is reused for every request.",
                namespaceId ?? "(personal)");

            _clients[key] = scoped;
            return scoped;
        }
    }
}

public sealed class DropBoxNotConfiguredException(string message) : Exception(message);