namespace Utility.Server.Finder;

/// <summary>
/// One group of parent file names: the path they sit in, and how many names it holds.
/// </summary>
/// <remarks>
/// The row of the Groups window's list (docs/finder-groups-spec.md §2).
/// </remarks>
public sealed record NameGroup
{
    /// <summary>
    /// The path the names sit in - <c>ml.Media.Path</c>, as it is stored.
    /// </summary>
    /// <remarks>
    /// The path is the group key. It is taken verbatim, but paths are compared
    /// case-insensitively, because the paths are directories on disk and the filesystem
    /// does not distinguish case.
    /// </remarks>
    public required string Path { get; init; }

    /// <summary>
    /// How many distinct parent file names the path holds (§2.2).
    /// </summary>
    public required int Count { get; init; }

    /// <summary>
    /// Whether the path's names carry more than one first-hyphen prefix (§2.4).
    /// </summary>
    /// <remarks>
    /// Such a path holds one product written two ways, or two products sharing a
    /// folder. The mark saves a monitor from spotting them by eye.
    /// </remarks>
    public bool HasTwoPrefixes { get; init; }

    /// <summary>The mark, as the reader sees it.</summary>
    public string Mark => HasTwoPrefixes ? "⚠" : string.Empty;
}
