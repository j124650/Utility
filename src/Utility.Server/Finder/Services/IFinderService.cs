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
    /// The rows are published to the scan state as they are produced, so the Finder tab and
    /// Liem's List share one pass instead of running two.
    /// </para>
    /// </remarks>
    /// <param name="progress">Receives the pass's progress, or null.</param>
    /// <param name="force">
    /// True to run the pass again even when the scan state already holds one. False returns
    /// the rows the last pass produced.
    /// </param>
    /// <param name="ct">Cancels the pass.</param>
    Task<IReadOnlyList<FinderRow>> GetMediaPairsAsync(
        IFinderProgress? progress = null,
        bool force = false,
        CancellationToken ct = default);

    /// <summary>
    /// The ranked candidates for one parent: the files whose name matches it, plus the ones
    /// whose name is similar when the name matched nothing (§9.1, §9.3, §10).
    /// </summary>
    /// <remarks>
    /// The pass gathers the candidates for every pair in one set-based join, because a lookup
    /// per parent would scan the Dropbox index once per pair. This is the single-parent form
    /// of the same rules, for a caller that wants one parent's candidates without a pass.
    /// </remarks>
    Task<IReadOnlyList<FinderCandidate>> GetDropBoxCandidatesAsync(
        string parentFileName,
        long? parentSize = null,
        string? parentFolder = null,
        CancellationToken ct = default);

    /// <summary>
    /// Liem's List: the pairs the scan leaves unresolved - no original found, or nothing to
    /// restore from at all (§11).
    /// </summary>
    /// <remarks>
    /// It is computed on the fly from the same scan that produced the Finder rows, so it is
    /// only as fresh as the last pass. A scan is run first if there is none.
    /// </remarks>
    Task<IReadOnlyList<FinderRow>> GetLiemListAsync(
        IFinderProgress? progress = null,
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
