using Utility.Server.Finder;

namespace Utility.UI.Services;

/// <summary>
/// The figures the confirmation window shows.
/// </summary>
/// <remarks>
/// Every figure is read off the rows the table is showing, and from what the recovery
/// would do to them. Nothing is queried: the window says exactly what the table shows and
/// exactly what the repair will do, so no second count can disagree with the first.
/// </remarks>
public sealed record RecoverConfirmation
{
    /// <summary>The rows the table is showing — the size of the filtered set.</summary>
    public required int Shown { get; init; }

    /// <summary>Of those, the ones whose verdict is 1, 2 or 3 — the subject of the recovery.</summary>
    public required int Recoverable { get; init; }

    /// <summary>How many parents will be restored from Dropbox (verdicts 1 and 3).</summary>
    public required int ParentRestored { get; init; }

    /// <summary>How many children will be regenerated (verdicts 2 and 3).</summary>
    public required int ChildRegenerated { get; init; }

    /// <summary>How many pairs will be restored and then regenerated (verdict 3).</summary>
    public required int Both { get; init; }

    /// <summary>How much will be downloaded.</summary>
    public required long DownloadBytes { get; init; }

    /// <summary>How many existing files will be replaced.</summary>
    public required int Replaced { get; init; }

    /// <summary>How many folders will be written to.</summary>
    public required int Folders { get; init; }

    /// <summary>How many pairs will be left alone.</summary>
    public required int NotRecovered { get; init; }

    /// <summary>Where the library lives - the root the files are written to.</summary>
    public required string LibraryRoot { get; init; }

    /// <summary>
    /// Whether the window warns that this will take a long time.
    /// </summary>
    /// <remarks>
    /// The threshold is 100 rows, and it is a constant rather than a setting. Below it the
    /// run is minutes; above it the reader should think about filtering more narrowly
    /// first.
    /// </remarks>
    public bool IsLongRun => Recoverable > 100;

    /// <summary>The download volume, as the reader sees it.</summary>
    public string DownloadDisplay => DownloadBytes >= 1024L * 1024 * 1024
        ? $"{DownloadBytes / 1024.0 / 1024 / 1024:N1} GB"
        : $"{DownloadBytes / 1024.0 / 1024:N1} MB";

    /// <summary>
    /// Reads the figures off the rows the table is showing.
    /// </summary>
    public static RecoverConfirmation From(IReadOnlyList<FinderRow> rows, string libraryRoot)
    {
        var subject = rows.Where(r => r.Verdict.Id is 1 or 2 or 3).ToList();

        var parentRestored = subject.Count(r => r.Verdict.Id is 1 or 3);
        var childRegenerated = subject.Count(r => r.Verdict.Id is 2 or 3);
        var both = subject.Count(r => r.Verdict.Id == 3);

        // The download figure is the confirmed candidate's size, not the parent's: the
        // confirmed candidate is the one the pass already selected by exact size match, and
        // it is the file that will actually be fetched.
        var download = subject
            .Where(r => r.Verdict.Id is 1 or 3)
            .Sum(r => r.BestCandidate?.Size ?? 0);

        // A corrupted file is overwritten, so a corrupted side is a file that will be
        // replaced.
        var replaced = subject.Sum(r =>
            (r.Parent.State == FileState.Corrupted ? 1 : 0) +
            (r.Child.State == FileState.Corrupted ? 1 : 0));

        var folders = subject.Select(r => r.ParentFolder).Distinct().Count();

        return new RecoverConfirmation
        {
            Shown = rows.Count,
            Recoverable = subject.Count,
            ParentRestored = parentRestored,
            ChildRegenerated = childRegenerated,
            Both = both,
            DownloadBytes = download,
            Replaced = replaced,
            Folders = folders,
            NotRecovered = rows.Count - subject.Count,
            LibraryRoot = libraryRoot,
        };
    }
}
