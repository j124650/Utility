namespace Utility.Server.Finder;

/// <summary>
/// Why a pair's original was not confirmed — the three reasons of §4.
/// </summary>
/// <remarks>
/// The reason is READ OFF the gathered row, never worked out a second time: the row
/// already carries the verdict and the candidates, and the reason follows from them.
/// <para>
/// The verdict splits the list in two, and the candidates split the second half in two
/// again: <c>cannot restore</c> is <see cref="NotFound"/>, and
/// <c>no original — needs a decision</c> is <see cref="SizeDiffers"/> when no candidate
/// carries the parent's recorded size, and <see cref="Ambiguous"/> when more than one
/// does.
/// </para>
/// </remarks>
public readonly record struct LiemListReason(int Id, string Text, string Color) : IComparable<LiemListReason>
{
    /// <summary>The file name is not in the index — there is nothing to restore from.</summary>
    public static readonly LiemListReason NotFound = new(0, "not found", "#B91C1C");

    /// <summary>The name was found, but no candidate carries the parent's recorded size.</summary>
    public static readonly LiemListReason SizeDiffers = new(1, "name found but size differs", "#B45309");

    /// <summary>More than one candidate carries the recorded size — the choice was not the only one.</summary>
    public static readonly LiemListReason Ambiguous = new(2, "ambiguous", "#4338CA");

    /// <summary>The three reasons, in the order of §4's table.</summary>
    public static readonly IReadOnlyList<LiemListReason> All =
    [
        NotFound,
        SizeDiffers,
        Ambiguous
    ];

    public int CompareTo(LiemListReason other) => Id.CompareTo(other.Id);

    /// <summary>
    /// The reason for a row, read off its verdict and its candidates.
    /// </summary>
    /// <remarks>
    /// Nothing here re-derives what the Finder already decided. The verdict says whether
    /// the pair is unresolved at all, and the candidates say whether the recorded size was
    /// carried by none of them or by more than one.
    /// </remarks>
    public static LiemListReason For(FinderRow row)
    {
        if (row.Verdict == FinderVerdict.CannotRestore)
        {
            // Nothing to restore from at all — the file name is not in the index.
            return NotFound;
        }

        var carrying = Carrying(row);

        return carrying > 1 ? Ambiguous : SizeDiffers;
    }

    /// <summary>
    /// How many of the row's candidates carry the parent's recorded size.
    /// </summary>
    /// <remarks>
    /// The size is the parent's RECORDED size — the length of the file as it was imported
    /// — because that is what a candidate has to match to be the original (§6.4). A row
    /// with no recorded size carries it on none of them.
    /// </remarks>
    private static int Carrying(FinderRow row)
    {
        var size = row.Parent.Size;

        if (!size.HasValue)
        {
            return 0;
        }

        var count = 0;

        foreach (var candidate in row.Candidates)
        {
            if (candidate.Size == size)
            {
                count++;
            }
        }

        return count;
    }

    public override string ToString() => Text;
}

/// <summary>One candidate of a pair, as the report shows it (§6.4).</summary>
public sealed record LiemListCandidate
{
    /// <summary>0–3, from the ranking rules (§10).</summary>
    public required int Rank { get; init; }

    /// <summary>The candidate's path, in full — never truncated.</summary>
    public required string Path { get; init; }

    /// <summary>The candidate's size, thousands-separated.</summary>
    public required string SizeDisplay { get; init; }

    /// <summary>The candidate's dimensions.</summary>
    public required string Dimensions { get; init; }

    /// <summary>What matched — the condition that produced the rank.</summary>
    public required string WhatMatched { get; init; }

    /// <summary>
    /// Whether this candidate's size equals the original size — the comparison the
    /// reader is making (§6.4).
    /// </summary>
    public required bool SizeMatches { get; init; }
}

/// <summary>One pair of a report — a parent and its child, unresolved (§6.3).</summary>
public sealed record LiemListPair
{
    /// <summary>The style: the parent's folder, verbatim (§3).</summary>
    public required string Style { get; init; }

    /// <summary>The parent's file name (§3).</summary>
    public required string FileName { get; init; }

    /// <summary>
    /// The angle or featured type, as its lines: the child's category name, and for
    /// <c>24 Angles</c> the variant that names the angle (§3, §6.3).
    /// </summary>
    public required IReadOnlyList<string> AngleLines { get; init; }

    /// <summary>
    /// The angle, when the child is one: <c>24 Angles</c>, or the angle's own name
    /// (<c>Angle 22</c>). Empty when the child is a featured shot
    /// (docs/liems-list-csv-spec.md §3).
    /// </summary>
    public required string Angle { get; init; }

    /// <summary>
    /// The featured type, when the child is one: <c>Featured</c>, <c>Top</c>,
    /// <c>Bottom</c>, <c>Thumbnail</c>, <c>Outsole</c>, <c>Back</c> or <c>Artwork</c>.
    /// Empty when the child is an angle (docs/liems-list-csv-spec.md §3).
    /// </summary>
    public required string FeaturedType { get; init; }

    /// <summary>When the parent row was added to the library — the row's date (§3).</summary>
    public required DateTimeOffset? DateAdded { get; init; }

    /// <summary>The parent's date added, as the reader sees it.</summary>
    public required string DateAddedDisplay { get; init; }

    /// <summary>The parent's recorded size, thousands-separated (§3).</summary>
    public required string OriginalSizeDisplay { get; init; }

    /// <summary>Why the original was not confirmed (§4).</summary>
    public required LiemListReason Reason { get; init; }

    /// <summary>The pair's candidates, in rank order (§6.4).</summary>
    public required IReadOnlyList<LiemListCandidate> Candidates { get; init; }
}

/// <summary>One style's section of the report — its unresolved pairs, newest first (§6.2).</summary>
public sealed record LiemListSection
{
    /// <summary>The style: the media library path (§3).</summary>
    public required string Style { get; init; }

    /// <summary>The style's unresolved pairs, newest first (§5).</summary>
    public required IReadOnlyList<LiemListPair> Pairs { get; init; }
}

/// <summary>How many of the report's pairs carry a reason (§6.2).</summary>
public sealed record LiemListReasonCount(LiemListReason Reason, int Count);

/// <summary>
/// Liem's List: the report of the pairs the verdict leaves unresolved (§1).
/// </summary>
/// <remarks>
/// It is a report on ONE pass — the rows the Finder's pass gathered — and it is only as
/// fresh as that pass, so the report names the pass it is on.
/// </remarks>
public sealed record LiemListReport
{
    /// <summary>The report's title: the list, and the pass it was made from (§6.7).</summary>
    public required string Title { get; init; }

    /// <summary>When the pass finished (§6.2).</summary>
    public required DateTimeOffset PassAt { get; init; }

    /// <summary>How many pairs the pass scanned (§6.2).</summary>
    public required int PairsScanned { get; init; }

    /// <summary>The unresolved pairs, broken down by reason (§6.2).</summary>
    public required IReadOnlyList<LiemListReasonCount> Breakdown { get; init; }

    /// <summary>One section per style, style ascending (§2, §5).</summary>
    public required IReadOnlyList<LiemListSection> Sections { get; init; }

    /// <summary>How many pairs the report holds.</summary>
    public int PairCount => Sections.Sum(s => s.Pairs.Count);
}
