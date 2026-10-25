namespace Utility.Server.Finder;

/// <summary>
/// One candidate for a pair's original: an indexed Dropbox file that might be it.
/// </summary>
public sealed record FinderCandidate
{
    /// <summary>0-3, from the ranking rules.</summary>
    public required int Rank { get; init; }

    /// <summary>The condition that produced the rank, e.g. <c>name and size</c>.</summary>
    public required string WhatMatched { get; init; }

    public required string Name { get; init; }

    public long? Size { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    /// <summary>The Dropbox path - what the preview fetches from.</summary>
    public required string Path { get; init; }

    /// <summary>When Dropbox last changed the file. Null when not recorded.</summary>
    public DateTimeOffset? Modified { get; init; }

    public string SizeDisplay => Size.HasValue ? Size.Value.ToString("N0") : string.Empty;

    public string DimensionsDisplay =>
        Width.HasValue && Height.HasValue ? $"{Width} × {Height}" : string.Empty;

    public string ModifiedDisplay =>
        Modified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? string.Empty;
}

/// <summary>
/// One pair of media rows - a parent and its child - with everything the UI shows about it.
/// </summary>
/// <remarks>
/// This is the row record. Every value the Finder tab displays is a field of it, so the UI
/// binds rather than re-derives.
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

    /// <summary>Why the verdict came out the way it did.</summary>
    public required string Note { get; init; }

    public required bool CheckParentOpens { get; init; }

    public required bool CheckChildOpens { get; init; }

    public required bool CheckCandidatesFound { get; init; }

    public required bool CheckOriginalConfirmed { get; init; }

    /// <summary>Null when no tiebreak was needed.</summary>
    public bool? CheckTiebreakApplied { get; init; }

    // ---- the candidates ----

    public required IReadOnlyList<FinderCandidate> Candidates { get; init; }

    public int CandidateCount => Candidates.Count;

    /// <summary>The highest-ranked candidate, or null when there are none.</summary>
    public FinderCandidate? BestCandidate => Candidates.Count > 0 ? Candidates[0] : null;

    /// <summary>The icon that stands for this row's verdict.</summary>
    public string Icon => Verdict switch
    {
        FinderVerdict.NothingToDo => "✓",
        FinderVerdict.RestoreParent => "↓",
        FinderVerdict.RegenerateChild => "↻",
        FinderVerdict.RestoreParentThenRegenerateChild => "↓ ↻",
        FinderVerdict.NoOriginalNeedsDecision => "?",
        FinderVerdict.CannotRestore => "✕",
        _ => string.Empty
    };

    /// <summary>The icon that stands for a file's state.</summary>
    public static string IconFor(FileState state) => state switch
    {
        FileState.Ok => "✓",
        FileState.Missing => "✕",
        FileState.Corrupted => "!",
        _ => string.Empty
    };
}

/// <summary>What the Finder would do about a pair. Calculated, never carried out.</summary>
public enum FinderVerdict
{
    NothingToDo = 0,
    RestoreParent = 1,
    RegenerateChild = 2,
    RestoreParentThenRegenerateChild = 3,
    NoOriginalNeedsDecision = 4,
    CannotRestore = 5
}
