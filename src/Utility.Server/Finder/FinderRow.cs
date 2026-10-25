namespace Utility.Server.Finder;

/// <summary>
/// One candidate for a pair's original: an indexed Dropbox file that might be it.
/// </summary>
/// <remarks>
/// The fields are §10's "One candidate's data". Nothing here is re-derived in the UI.
/// </remarks>
public sealed record FinderCandidate
{
    /// <summary>0-3, from the ranking rules (§10).</summary>
    public required int Rank { get; init; }

    /// <summary>The condition that produced the rank, e.g. <c>name and size</c>.</summary>
    public required string WhatMatched { get; init; }

    /// <summary>The file's name in the Dropbox index.</summary>
    public required string Name { get; init; }

    /// <summary>The Dropbox file's size in bytes (<c>SizeBytes</c>).</summary>
    public required long? Size { get; init; }

    /// <summary>The file's dimensions, as §10 words them: <c>WidthPixels × HeightPixels</c>.</summary>
    public required string Dimensions { get; init; }

    /// <summary>The Dropbox path (<c>PathDisplay</c>) - what the preview fetches from.</summary>
    public required string Path { get; init; }

    /// <summary>When Dropbox last changed the file (<c>ServerModified</c>).</summary>
    public required DateTimeOffset? Modified { get; init; }

    /// <summary>The size in bytes, as the reader sees it.</summary>
    public string SizeDisplay => Size.HasValue ? Size.Value.ToString("N0") : string.Empty;

    /// <summary>The modification date, as the reader sees it. Empty when there is none.</summary>
    public string ModifiedDisplay =>
        Modified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? string.Empty;
}

/// <summary>
/// One pair of media rows - a parent and its child - with everything the UI shows about it.
/// </summary>
/// <remarks>
/// This is the row record (§19.1). Every value the Finder tab displays is a field of it, so
/// the UI binds rather than re-derives.
/// </remarks>
public sealed record FinderRow
{
    // ---- identity, from ml.media ----

    public required string ParentFileName { get; init; }

    public required string ParentFolder { get; init; }

    public required Guid ParentId { get; init; }

    public required Guid ChildId { get; init; }

    public short? ChildCategory { get; init; }

    public short? ChildVariant { get; init; }

    // ---- each side ----

    public required FinderFileState Parent { get; init; }

    public required FinderFileState Child { get; init; }

    // ---- the verdict and the checks behind it ----

    public required FinderVerdict Verdict { get; init; }

    /// <summary>Why the verdict came out the way it did (§19.9).</summary>
    public required string VerdictNote { get; init; }

    /// <summary>The icon for the verdict (§18.4).</summary>
    public string VerdictIcon => Verdict.Icon;

    public required FinderCheck CheckParentOpens { get; init; }

    public required FinderCheck CheckChildOpens { get; init; }

    public required FinderCheck CheckCandidatesFound { get; init; }

    public required FinderCheck CheckOriginalConfirmed { get; init; }

    /// <summary>Empty when no tiebreak was needed (§7, §19.1).</summary>
    public FinderCheck? CheckTiebreakApplied { get; init; }

    // ---- the candidates ----

    /// <summary>The display set, ranked (§10, §19.1).</summary>
    public required IReadOnlyList<FinderCandidate> Candidates { get; init; }

    /// <summary>How many candidates the pair has.</summary>
    public int CandidateCount => Candidates.Count;

    /// <summary>
    /// The highest-ranked candidate - <c>Candidates[0]</c> - or null when there are none.
    /// </summary>
    /// <remarks>
    /// The table's candidate columns are this candidate's fields (§14.2, §19.1). It is a
    /// property rather than an indexer so a pair with no candidates shows an empty cell
    /// instead of raising an index error on every binding.
    /// </remarks>
    public FinderCandidate? BestCandidate => Candidates.Count > 0 ? Candidates[0] : null;
}

/// <summary>
/// What the Finder would do about a pair (§7). Calculated, never carried out.
/// </summary>
/// <remarks>
/// A value rather than an enum, because the wording is what the reader sees: the verdict is
/// spelled out ("no original — needs a decision"), not a code the table would have to be
/// decoded against.
/// </remarks>
public readonly record struct FinderVerdict(int Id, string Text, string Icon) : IComparable<FinderVerdict>
{
    /// <summary>Both files are ok - nothing to do.</summary>
    public static readonly FinderVerdict NothingToDo = new(0, "nothing to do", "✓");

    /// <summary>The parent needs restoring from Dropbox.</summary>
    public static readonly FinderVerdict RestoreParent = new(1, "restore parent", "↓");

    /// <summary>The child needs regenerating from its parent.</summary>
    public static readonly FinderVerdict RegenerateChild = new(2, "regenerate child", "↻");

    /// <summary>Both need work.</summary>
    public static readonly FinderVerdict RestoreParentThenRegenerateChild =
        new(3, "restore parent, then regenerate child", "↓ ↻");

    /// <summary>No original matched by size - a human has to choose a candidate.</summary>
    public static readonly FinderVerdict NoOriginalNeedsDecision =
        new(4, "no original — needs a decision", "?");

    /// <summary>There is nothing to restore from.</summary>
    public static readonly FinderVerdict CannotRestore = new(5, "cannot restore", "✕");

    /// <summary>Every verdict, in the order of §7's table.</summary>
    public static readonly IReadOnlyList<FinderVerdict> All =
    [
        NothingToDo,
        RestoreParent,
        RegenerateChild,
        RestoreParentThenRegenerateChild,
        NoOriginalNeedsDecision,
        CannotRestore
    ];

    public int CompareTo(FinderVerdict other) => Id.CompareTo(other.Id);

    public override string ToString() => Text;
}
