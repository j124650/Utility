namespace Utility.Server.Finder;

/// <summary>
/// The filters of §14.3, and the rule that decides whether a row passes them.
/// </summary>
/// <remarks>
/// The whole set is loaded, so filtering happens in memory over every row. The filter is
/// here, on the row record's own assembly, so the table and any other reader of the rows
/// filter them the same way.
/// </remarks>
public sealed class FinderFilter
{
    /// <summary>
    /// A substring of the parent's folder. Null or empty keeps every folder (§14.3).
    /// </summary>
    public string? ParentFolderText { get; set; }

    /// <summary>
    /// A substring of the parent's file name. Null or empty keeps every file name (§14.3).
    /// </summary>
    public string? ParentFileNameText { get; set; }

    /// <summary>The verdicts to keep. Empty keeps every verdict.</summary>
    public HashSet<FinderVerdict> Verdicts { get; } = [];

    /// <summary>The parent states to keep. Empty keeps every state.</summary>
    public HashSet<FileState> ParentStates { get; } = [];

    /// <summary>The child states to keep. Empty keeps every state.</summary>
    public HashSet<FileState> ChildStates { get; } = [];

    /// <summary>The candidate count to keep. Null keeps every count.</summary>
    public int? CandidateCount { get; set; }

    /// <summary>The child category to keep. Null keeps every category.</summary>
    public short? ChildCategory { get; set; }

    /// <summary>The child variant to keep. Null keeps every variant.</summary>
    public short? ChildVariant { get; set; }

    /// <summary>The best candidate's rank to keep. Null keeps every rank.</summary>
    public int? BestCandidateRank { get; set; }

    /// <summary>Whether the row passes every filter.</summary>
    /// <remarks>
    /// The cheap tests come first: a row that fails on its verdict or its states never
    /// reaches the text search over the candidates.
    /// </remarks>
    public bool Matches(FinderRow row)
    {
        if (Verdicts.Count > 0 && !Verdicts.Contains(row.Verdict))
        {
            return false;
        }

        if (ParentStates.Count > 0 && !ParentStates.Contains(row.Parent.State))
        {
            return false;
        }

        if (ChildStates.Count > 0 && !ChildStates.Contains(row.Child.State))
        {
            return false;
        }

        if (CandidateCount.HasValue && row.CandidateCount != CandidateCount.Value)
        {
            return false;
        }

        if (ChildCategory.HasValue && row.ChildCategory != ChildCategory.Value)
        {
            return false;
        }

        if (ChildVariant.HasValue && row.ChildVariant != ChildVariant.Value)
        {
            return false;
        }

        if (BestCandidateRank.HasValue && row.BestCandidate?.Rank != BestCandidateRank.Value)
        {
            return false;
        }

        var folder = ParentFolderText?.Trim();
        if (!string.IsNullOrEmpty(folder) && !Contains(row.ParentFolder, folder))
        {
            return false;
        }

        var fileName = ParentFileNameText?.Trim();
        if (!string.IsNullOrEmpty(fileName) && !Contains(row.ParentFileName, fileName))
        {
            return false;
        }

        return true;
    }

    private static bool Contains(string? value, string text) =>
        value is not null && value.Contains(text, StringComparison.OrdinalIgnoreCase);
}
