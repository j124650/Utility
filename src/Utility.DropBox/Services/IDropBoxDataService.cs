using Dropbox.Api;
using Dropbox.Api.Files;
using Microsoft.Extensions.Logging;
using Utility.DropBox.Models;

namespace Utility.DropBox.Services;

/// <summary>
/// Read-only Dropbox access. The utility pulls files on demand; it never uploads,
/// deletes or creates anything in the user's Dropbox.
/// <para>
/// Callers never see the SDK's <c>DropboxClient</c> or build queries against it.
/// </para>
/// </summary>
public interface IDropBoxDataService
{
    /// <summary>True when an App Key and token are configured.</summary>
    bool IsConfigured { get; }

    Task<AccountInfo> GetAccountAsync(CancellationToken ct = default);

    /// <summary>
    /// Namespaces the account can browse — personal space first, then the team root.
    /// Team folders are only reachable from the team namespace.
    /// </summary>
    Task<IReadOnlyList<DropBoxNamespace>> GetNamespacesAsync(CancellationToken ct = default);

    /// <summary>
    /// Lists one folder level. Folders come first, then files, each alphabetically.
    /// <para>
    /// Pass <paramref name="namespaceId"/> to browse a team namespace; omit it for the
    /// account's personal space. Use <c>""</c> for that namespace's root.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<FileEntry>> ListFolderAsync(
        string path = "",
        string? namespaceId = null,
        bool includeMediaInfo = false,
        CancellationToken ct = default);

    /// <summary>Full metadata for a single file or folder.</summary>
    Task<FileEntry> GetFileInfoAsync(
        string path,
        string? namespaceId = null,
        bool includeMediaInfo = true,
        CancellationToken ct = default);

    /// <summary>
    /// Builds the folder tree under <paramref name="path"/> — structure plus per-file
    /// metadata — as hierarchical JSON.
    /// <para>
    /// Sibling folders are walked concurrently against one shared client (verified safe
    /// for concurrent <c>ListFolder</c>). Cursor pages within a single folder stay
    /// sequential: a page cannot be requested without the previous page's cursor, so the
    /// parallelism is across folders, never within one.
    /// </para>
    /// </summary>
    Task<DropBoxFolderTree> GetFolderTreeAsync(
        string path = "",
        string? namespaceId = null,
        int maxDepth = 3,
        int maxConcurrency = 6,
        bool includeMediaInfo = false,
        CancellationToken ct = default);

    /// <summary>Downloads a file to disk, creating parent directories as needed.</summary>
    Task<string> DownloadAsync(
        string dropboxPath,
        string destinationPath,
        string? namespaceId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Downloads a file into memory. For a preview that is shown and discarded - nothing is
    /// written to disk.
    /// </summary>
    Task<byte[]> DownloadBytesAsync(
        string dropboxPath,
        string? namespaceId = null,
        CancellationToken ct = default);
}

public sealed class DropBoxDataService(
    IDropBoxClientFactory clientFactory,
    ILogger<DropBoxDataService> logger) : IDropBoxDataService
{
    public bool IsConfigured => clientFactory.IsConfigured;

    public async Task<AccountInfo> GetAccountAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var client = await clientFactory.CreateValidatedClientAsync(ct);

        var account = await client.Users.GetCurrentAccountAsync();

        // Space usage is a separate endpoint; don't let it break the account read.
        Dropbox.Api.Users.SpaceUsage? usage = null;
        try
        {
            usage = await client.Users.GetSpaceUsageAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read Dropbox space usage.");
        }

        return AccountInfo.From(account, usage);
    }

    public async Task<IReadOnlyList<DropBoxNamespace>> GetNamespacesAsync(CancellationToken ct = default)
    {
        var account = await GetAccountAsync(ct);
        return account.GetNamespaces();
    }

    public async Task<IReadOnlyList<FileEntry>> ListFolderAsync(
        string path = "", string? namespaceId = null, bool includeMediaInfo = false,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Scoping the client to the namespace is what makes team folders visible;
        // without it Dropbox resolves the path in the personal space and 404s.
        var client = await clientFactory.CreateClientForNamespaceAsync(namespaceId, ct);

        IReadOnlyList<FileEntry> entries;
        try
        {
            entries = await ListLevelAsync(client, path, includeMediaInfo, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Translate(ex);
        }

        return entries
            .OrderByDescending(x => x.IsFolder)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Translates an SDK failure into an app-owned exception.
    /// <para>
    /// Three outcomes, and the caller must be able to tell them apart:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>RateLimitException</c> - HTTP 429. <b>The SDK does NOT retry these</b>: its
    /// retry budget covers server errors only (a 500 is retried, a 429 is raised
    /// straight through). So this is the caller's problem to solve.
    /// </description></item>
    /// <item><description>
    /// <c>path/not_found</c> - the path itself is gone or invisible. Permanent: listing
    /// it again fails the same way.
    /// </description></item>
    /// <item><description>
    /// anything else - a dropped connection, a TLS failure, a server fault. Transient:
    /// it may well succeed on the next attempt.
    /// </description></item>
    /// </list>
    /// <para>
    /// The SDK's own <c>RateLimitException.RetryAfter</c> throws a
    /// <c>NullReferenceException</c> when the body did not parse into a rate-limit
    /// error, so the parsed <c>ErrorResponse</c> is read instead - and it carries the
    /// value from the BODY's <c>retry_after</c>, not from the HTTP header. When there
    /// is no parsed body either, the caller gets zero and picks its own backoff.
    /// </para>
    /// </summary>
    private static Exception Translate(Exception ex) => ex switch
    {
        RateLimitException rateLimited => new DropBoxRateLimitedException(
            ReadRetryAfter(rateLimited),
            rateLimited.Message,
            rateLimited),

        // path/not_found arrives as an ApiException whose error union carries the
        // lookup error. The tag is what says "the path is gone" as opposed to
        // "malformed path" or "not a folder".
        //
        // The union nests: ListFolderError.Path wraps a LookupError, which is where
        // the not-found tag lives.
        ApiException<Dropbox.Api.Files.ListFolderError> listFolder
            when listFolder.ErrorResponse.IsPath
                 && listFolder.ErrorResponse.AsPath.Value.IsNotFound
            => new DropBoxNotFoundException(listFolder.Message, listFolder),

        _ => ex
    };

    /// <summary>
    /// The wait Dropbox asked for, in seconds, or zero when it did not say.
    /// <para>
    /// Read from the parsed error body, which is the only place the SDK exposes it.
    /// The property on the exception itself throws when the body did not parse, so it
    /// is never read.
    /// </para>
    /// </summary>
    private static int ReadRetryAfter(RateLimitException ex)
    {
        try
        {
            var parsed = ex.ErrorResponse;
            if (parsed is null)
            {
                return 0;
            }

            // The body carries it in seconds. Guard the cast: a hostile or malformed
            // body must not become a negative wait.
            var seconds = parsed.RetryAfter;
            return seconds > int.MaxValue ? int.MaxValue : (int)seconds;
        }
        catch
        {
            return 0;
        }
    }

    public async Task<FileEntry> GetFileInfoAsync(
        string path, string? namespaceId = null, bool includeMediaInfo = true,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A path is required.", nameof(path));
        }

        var client = await clientFactory.CreateClientForNamespaceAsync(namespaceId, ct);

        // VERIFIED BEHAVIOUR, unlike list_folder: include_media_info is IGNORED by
        // files/list_folder (tested non-recursive, recursive and in the personal namespace -
        // all returned zero media_info). get_metadata DOES honour it and returns real
        // dimensions, e.g. 2000x2500 plus time_taken. That is why per-file metadata is
        // the only working route to dimensions, and why it costs one call per file.
        //
        // Translated like the listing is: this is where most of a sync's requests go,
        // so it is where a rate limit will be hit first.
        Metadata metadata;
        try
        {
            metadata = await client.Files.GetMetadataAsync(
                path, includeMediaInfo: includeMediaInfo);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Translate(ex);
        }

        return FileEntry.From(metadata);
    }

    public async Task<DropBoxFolderTree> GetFolderTreeAsync(
        string path = "",
        string? namespaceId = null,
        int maxDepth = 3,
        int maxConcurrency = 6,
        bool includeMediaInfo = false,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);

        // One client for the entire walk: concurrent ListFolder calls on a single client
        // are verified safe, and it keeps token refresh state in one place.
        var client = await clientFactory.CreateClientForNamespaceAsync(namespaceId, ct);

        // One gate bounds in-flight requests across every level of the recursion, so a
        // wide tree cannot open hundreds of sockets at once.
        using var gate = new SemaphoreSlim(maxConcurrency);
        var started = DateTimeOffset.UtcNow;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var nodes = await BuildLevelAsync(client, path, depth: 1, maxDepth, includeMediaInfo, gate, ct);
        sw.Stop();

        var folders = nodes.Where(x => x.IsFolder).ToList();
        var fileCount = nodes.Sum(x => x.TotalFileCount);
        var totalBytes = nodes.Sum(x => x.TotalSizeBytes);

        logger.LogInformation(
            "Built Dropbox tree at {Path} (depth {Depth}) in {ElapsedMs} ms: {Folders} folder(s), {Files} file(s).",
            path, maxDepth, sw.ElapsedMilliseconds, folders.Count, fileCount);

        return new DropBoxFolderTree(
            RootPath: string.IsNullOrEmpty(path) ? "/" : path,
            NamespaceId: namespaceId ?? "(personal)",
            MaxDepth: maxDepth,
            StartedUtc: started,
            DurationMs: sw.ElapsedMilliseconds,
            FolderCount: folders.Count,
            FileCount: fileCount,
            TotalSizeBytes: totalBytes,
            Nodes: nodes);
    }

    /// <summary>
    /// Expands one folder level: files become leaf nodes, folders recurse concurrently
    /// while under <paramref name="maxDepth"/>.
    /// </summary>
    private static async Task<List<DropBoxNode>> BuildLevelAsync(
        DropboxClient client,
        string path,
        int depth,
        int maxDepth,
        bool includeMediaInfo,
        SemaphoreSlim gate,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var entries = await ListLevelAsync(client, path, includeMediaInfo, ct);

        var fileNodes = entries
            .Where(x => !x.IsFolder)
            .Select(DropBoxNode.From)
            .ToList();

        var folderEntries = entries.Where(x => x.IsFolder).ToList();

        // At the depth cap, emit folders unexpanded: the structure still shows where
        // content continues, without paying for another full level of requests.
        if (depth >= maxDepth || folderEntries.Count == 0)
        {
            return fileNodes.Concat(folderEntries.Select(DropBoxNode.From)).ToList();
        }

        var childTasks = folderEntries.Select(async folder =>
        {
            await gate.WaitAsync(ct);
            try
            {
                // folder.PathDisplay is already absolute within the namespace root —
                // never re-prefix it, or Dropbox answers path/not_found.
                var children = await BuildLevelAsync(
                    client, folder.PathDisplay, depth + 1, maxDepth, includeMediaInfo, gate, ct);

                return DropBoxNode.From(folder).WithChildren(children);
            }
            finally
            {
                gate.Release();
            }
        });

        var expanded = await Task.WhenAll(childTasks);

        // Folders first, then files, each alphabetical — matches the flat listing order.
        return expanded.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Concat(fileNodes.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>Reads one folder level, following every cursor page to the end.</summary>
    private static async Task<List<FileEntry>> ListLevelAsync(
        DropboxClient client, string path, bool includeMediaInfo, CancellationToken ct)
    {
        // includeMediaInfo makes Dropbox do extra work per file, so it is opt-in and defaults
        // to false. The cost on this account has NOT been measured - a benchmark attempt
        // was abandoned as too slow. Do not flip the default without measuring it; see
        // Sql/AlterAddImageDimensions.sql for the full warning and the MediaInfoTag
        // semantics that make a NULL width/height honest.
        var result = await client.Files.ListFolderAsync(
            path, recursive: false, includeMediaInfo: includeMediaInfo);
        var entries = new List<FileEntry>(result.Entries.Count);

        foreach (var metadata in result.Entries)
        {
            ct.ThrowIfCancellationRequested();
            entries.Add(FileEntry.From(metadata));
        }

        while (result.HasMore)
        {
            ct.ThrowIfCancellationRequested();
            result = await client.Files.ListFolderContinueAsync(result.Cursor);

            foreach (var metadata in result.Entries)
            {
                ct.ThrowIfCancellationRequested();
                entries.Add(FileEntry.From(metadata));
            }
        }

        return entries;
    }

    public async Task<string> DownloadAsync(
        string dropboxPath, string destinationPath, string? namespaceId = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Same namespace scoping as listing — a team file must be fetched from the team
        // namespace or Dropbox reports it as missing.
        var client = await clientFactory.CreateClientForNamespaceAsync(namespaceId, ct);

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var response = await client.Files.DownloadAsync(dropboxPath);
        await using var source = await response.GetContentAsStreamAsync()
            ?? throw new InvalidOperationException($"Dropbox returned no content stream for '{dropboxPath}'.");
        await using var destination = File.Create(destinationPath);

        await source.CopyToAsync(destination, ct);
        logger.LogInformation("Downloaded {DropboxPath} to {Destination}.", dropboxPath, destinationPath);

        return destinationPath;
    }

    public async Task<byte[]> DownloadBytesAsync(
        string dropboxPath, string? namespaceId = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Same namespace scoping as DownloadAsync — a team file fetched without it resolves
        // in the personal space and comes back as path/not_found.
        var client = await clientFactory.CreateClientForNamespaceAsync(namespaceId, ct);

        using var response = await client.Files.DownloadAsync(dropboxPath);
        await using var source = await response.GetContentAsStreamAsync()
            ?? throw new InvalidOperationException($"Dropbox returned no content stream for '{dropboxPath}'.");

        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, ct);
        logger.LogInformation("Downloaded {DropboxPath} to memory ({Bytes} bytes).", dropboxPath, buffer.Length);

        return buffer.ToArray();
    }
}