using Utility.Server.Finder;

namespace Utility.Server.Finder.Services;

/// <summary>
/// Gathers the pairs and everything the Finder tab shows about them.
/// </summary>
/// <remarks>
/// Every read goes through this service; callers never see a DbContext or build queries.
/// The ml schema is owned by another system, so nothing here mutates.
/// </remarks>
public interface IFinderService
{
    /// <summary>
    /// Gathers every pair and computes its verdict.
    /// </summary>
    /// <remarks>
    /// The pass runs in four phases: one set-based query for the pairs and their name-matched
    /// candidates, then a visit to every file on disk, then the similarity widening for the
    /// pairs that need it, then the verdict and the row. It is long - it opens every parent and
    /// child file in the library - and reports progress as it goes.
    /// <para>
    /// <paramref name="maxPairs"/> bounds the pass to the first N pairs. It exists so a run can
    /// be bounded while the pass is being tuned; null means every pair.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<FinderRow>> GetRowsAsync(
        IFinderProgress? progress = null,
        int? maxPairs = null,
        CancellationToken ct = default);
}

/// <summary>Reports the pass's progress back to the caller.</summary>
public interface IFinderProgress
{
    /// <summary>Called as the pass advances.</summary>
    void Report(FinderProgressInfo info);
}

/// <summary>Where the pass has reached.</summary>
public sealed record FinderProgressInfo(
    string Stage,
    int Done,
    int Total,
    string Detail);
