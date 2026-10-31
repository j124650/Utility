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
    /// Re-reads the pairs it is given: their files are visited again and their verdicts are
    /// decided again (§10.9).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the pass, narrowed to the pairs it names. The files are visited and the
    /// verdicts are decided exactly as the full pass decides them, so a row comes back
    /// carrying the verdict its files now deserve.
    /// </para>
    /// <para>
    /// The rows are NOT published to the scan state: the state holds the whole library, and
    /// publishing a narrowed set into it would make a later read of the state return only
    /// that set.
    /// </para>
    /// </remarks>
    /// <param name="parentIds">The parents to re-read, or null to re-read them all.</param>
    /// <param name="progress">Receives the pass's progress, or null.</param>
    /// <param name="ct">Cancels the pass.</param>
    Task<IReadOnlyList<FinderRow>> RereadMediaPairsAsync(
        IReadOnlyList<Guid>? parentIds,
        IFinderProgress? progress = null,
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

    /// <summary>
    /// The groups of parent file names: one group per path the parents sit in, with the
    /// number of distinct names in each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is computed from the rows the last pass produced - the whole gathered set, not
    /// the rows the table is showing - so it queries nothing and runs no pass of its own.
    /// It is empty when no pass has produced rows.
    /// </para>
    /// <para>
    /// The paths are directories on disk, so a path and the same path in another case are
    /// the same folder: the paths are compared case-insensitively.
    /// </para>
    /// </remarks>
    IReadOnlyList<NameGroup> GetNameGroups();

    /// <summary>
    /// The rows in the order the recovery works through them: most recent first (§10.11).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each pair is dated by the MORE RECENT of the two dates it carries - the media row's
    /// own date added, and the Dropbox file's modification date - and the pairs are sorted
    /// by that date, descending. A pair with neither date goes last.
    /// </para>
    /// <para>
    /// Ties are broken by the pair's file name, case-insensitively, so the order is
    /// deterministic: the same set always recovers in the same order, and the confirmation
    /// window can promise an order the run keeps.
    /// </para>
    /// </remarks>
    IReadOnlyList<FinderRow> OrderByMostRecent(IEnumerable<FinderRow> rows);
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
