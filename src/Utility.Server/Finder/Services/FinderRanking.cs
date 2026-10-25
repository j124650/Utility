namespace Utility.Server.Finder.Services;

/// <summary>
/// Ranks a pair's candidates and picks the most probable original.
/// </summary>
public static class FinderRanking
{
    /// <summary>
    /// Ranks a candidate set, best first.
    /// </summary>
    /// <remarks>
    /// Rank 0 is the strongest: the name matches, the size matches, AND the candidate sits
    /// in the folder the media library records for the parent. Within a rank, the order is
    /// by <c>final</c> in the path first, then the most recently modified, then the path
    /// depth, then the path itself.
    /// </remarks>
    public static IReadOnlyList<FinderCandidate> Rank(
        IReadOnlyList<CandidateSeed> seeds,
        string parentFileName,
        long? parentSize,
        string? parentFolder = null)
    {
        // The library's folder IS the Dropbox product folder: a candidate's path contains
        // it when the candidate is the file that was imported (§9.1 rule 3). An unknown
        // folder cannot disprove that, so the condition is satisfied.
        var folder = parentFolder ?? string.Empty;

        var ranked = seeds
            .Select(s => new
            {
                Seed = s,
                NameMatches = string.Equals(s.Name, parentFileName, StringComparison.OrdinalIgnoreCase),
                SizeMatches = parentSize.HasValue && s.Size == parentSize,
                FolderMatches = folder.Length == 0 || s.Path.Contains(folder, StringComparison.OrdinalIgnoreCase)
            })
            .Select(x => new
            {
                x.Seed,
                x.NameMatches,
                x.SizeMatches,
                x.FolderMatches,
                Rank = x.NameMatches && x.SizeMatches && x.FolderMatches ? 0
                    : x.SizeMatches ? 1
                    : x.NameMatches ? 2
                    : 3
            })
            .OrderBy(x => x.Rank)
            .ThenByDescending(x => HasFinal(x.Seed.Path))
            .ThenByDescending(x => x.Seed.Modified ?? DateTimeOffset.MinValue)
            .ThenBy(x => PathDepth(x.Seed.Path))
            .ThenBy(x => x.Seed.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return ranked
            .Select(x => new FinderCandidate
            {
                Rank = x.Rank,
                WhatMatched = WhatMatched(x.NameMatches, x.SizeMatches, x.FolderMatches),
                Name = x.Seed.Name,
                Size = x.Seed.Size,
                Dimensions = FinderFileState.FormatDimensions(x.Seed.Width, x.Seed.Height),
                Path = x.Seed.Path,
                Modified = x.Seed.Modified
            })
            .ToList();
    }

    /// <summary>
    /// The candidates whose name matches the parent's file name - the set the size rule and
    /// the tiebreak choose from.
    /// </summary>
    public static IReadOnlyList<CandidateSeed> SelectionSet(
        IReadOnlyList<CandidateSeed> candidates,
        string parentFileName) =>
        candidates
            .Where(c => string.Equals(c.Name, parentFileName, StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static string WhatMatched(bool nameMatches, bool sizeMatches, bool folderMatches) => (nameMatches, sizeMatches, folderMatches) switch
    {
        (true, true, true) => "name and size",
        (true, true, false) => "name and size, different folder",
        (false, true, _) => "size only",
        (true, false, _) => "name only",
        _ => "similar name"
    };

    private static bool HasFinal(string path) =>
        path.Contains("final", StringComparison.OrdinalIgnoreCase);

    private static int PathDepth(string path) => path.Count(c => c == '/');
}

/// <summary>A candidate before it is ranked.</summary>
public sealed record CandidateSeed(
    string Name,
    long? Size,
    int? Width,
    int? Height,
    string Path,
    DateTimeOffset? Modified);
