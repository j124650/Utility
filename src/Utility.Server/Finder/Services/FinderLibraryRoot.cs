namespace Utility.Server.Finder.Services;

/// <summary>
/// Works out where the media library actually is on local storage.
/// </summary>
/// <remarks>
/// <para>
/// <c>ml.Settings.MediaLibraryLocalPath</c> names the local root, and
/// <c>ml.Settings.MediaLibraryUncPath</c> names the same library over the network. The two do
/// not always describe the same shape of path: the local root may hold the library's folders
/// directly, or it may hold them under a folder named after the network share - which is what
/// a recovered copy looks like, where <c>C:\_Recover\</c> holds <c>media\</c> and
/// <c>PhotoLab\</c> side by side.
/// </para>
/// <para>
/// So the root is not assumed. It is resolved against the folders the media rows actually
/// name: the candidate whose directories are really there wins.
/// </para>
/// </remarks>
public static class FinderLibraryRoot
{
    /// <summary>
    /// The root, given the configured local path, the configured UNC path, and the folder
    /// names the media rows carry.
    /// </summary>
    /// <param name="localPath">ml.Settings.MediaLibraryLocalPath.</param>
    /// <param name="uncPath">ml.Settings.MediaLibraryUncPath.</param>
    /// <param name="mediaFolders">The distinct ml.Media.Path values.</param>
    public static string Resolve(
        string localPath,
        string? uncPath,
        IReadOnlySet<string> mediaFolders)
    {
        var candidates = new List<string> { localPath };

        // The UNC path's server is dropped and the rest is appended to the local root: the
        // local root mirrors the servers, each share appearing as a folder of the same name.
        // \\bbcvpn22\media -> <local>\media, \\webmisc2\PhotoLab\Inbox -> <local>\PhotoLab\Inbox.
        var mirrored = PathAfterServer(uncPath);
        if (mirrored is not null)
        {
            candidates.Add(Path.Combine(localPath, mirrored));
        }

        if (mediaFolders.Count == 0)
        {
            return candidates[0];
        }

        var best = candidates[0];
        var bestHits = -1;

        foreach (var candidate in candidates)
        {
            var hits = CountMediaFolders(candidate, mediaFolders);
            if (hits > bestHits)
            {
                bestHits = hits;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// How many of the candidate's immediate subdirectories are folders the media rows name.
    /// </summary>
    /// <remarks>
    /// Counted the other way round on purpose. The library was destroyed, so most of its
    /// folders are gone - probing every known folder name would be tens of thousands of stat
    /// calls to find the handful that survived. Listing the candidate's own directories and
    /// asking which of them are known is a single directory read.
    /// </remarks>
    private static int CountMediaFolders(string candidate, IReadOnlySet<string> mediaFolders)
    {
        if (!Directory.Exists(candidate))
        {
            return 0;
        }

        var hits = 0;

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(candidate))
            {
                var name = Path.GetFileName(directory);
                if (mediaFolders.Contains(name))
                {
                    hits++;
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            return hits;
        }
        catch (IOException)
        {
            return hits;
        }

        return hits;
    }

    /// <summary>
    /// The part of a UNC path after the server - the share and anything below it.
    /// </summary>
    /// <remarks>
    /// <c>\\bbcvpn22\media</c> gives <c>media</c>; <c>\\webmisc2\PhotoLab\Inbox</c> gives
    /// <c>PhotoLab\Inbox</c>. Null when the path is not a UNC path.
    /// </remarks>
    public static string? PathAfterServer(string? uncPath)
    {
        if (string.IsNullOrWhiteSpace(uncPath))
        {
            return null;
        }

        var trimmed = uncPath.Trim().TrimStart('\\');
        var slash = trimmed.IndexOf('\\');

        if (slash < 0)
        {
            // \\server only - nothing below it to mirror.
            return null;
        }

        var rest = trimmed[(slash + 1)..].TrimEnd('\\');
        return string.IsNullOrWhiteSpace(rest) ? null : rest;
    }
}
