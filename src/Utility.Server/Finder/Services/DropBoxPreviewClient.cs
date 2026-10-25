using Microsoft.EntityFrameworkCore;
using Utility.DropBox.Services;
using Utility.Server.DropBoxSync;

namespace Utility.Server.Finder.Services;

/// <summary>
/// Fetches a candidate's image from Dropbox, for the preview.
/// </summary>
public interface IDropBoxPreviewClient
{
    /// <summary>
    /// Downloads the file's bytes. Fetched, shown and discarded - never written to the media
    /// library.
    /// </summary>
    Task<byte[]?> DownloadAsync(string dropboxPath, CancellationToken ct = default);
}

/// <summary>
/// Downloads candidate images from Dropbox.
/// </summary>
/// <remarks>
/// The client is scoped to the namespace the file actually lives in. A team file fetched
/// without that scope resolves in the personal space and comes back as
/// <c>path/not_found</c>.
/// </remarks>
public sealed class DropBoxPreviewClient(
    IDropBoxDataService dropBox,
    IDbContextFactory<DropBoxDbContext> contextFactory) : IDropBoxPreviewClient
{
    public async Task<byte[]?> DownloadAsync(string dropboxPath, CancellationToken ct = default)
    {
        var namespaceId = await FindNamespaceAsync(dropboxPath, ct);
        if (namespaceId is null)
        {
            return null;
        }

        try
        {
            return await dropBox.DownloadBytesAsync(dropboxPath, namespaceId, ct);
        }
        catch (Exception)
        {
            // A preview that cannot be fetched is not an error the user needs to act on -
            // the row still shows everything else about the candidate.
            return null;
        }
    }

    /// <summary>
    /// The namespace the file is indexed under - read from the index, so nothing about the
    /// account is hardcoded here.
    /// </summary>
    private async Task<string?> FindNamespaceAsync(string dropboxPath, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var lower = dropboxPath.ToLowerInvariant();

        return await db.Nodes
            .AsNoTracking()
            .Where(n => n.PathLower == lower)
            .Select(n => n.NamespaceId)
            .FirstOrDefaultAsync(ct);
    }
}
