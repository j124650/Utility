using ImageMagick;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Utility.DropBox.Services;
using Utility.Server.Finder.Services;
using Utility.Server.ML.Services;
using Utility.Server.Options;

namespace Utility.Server.Finder.Repair;

/// <summary>
/// Restores what the library is missing.
/// </summary>
public interface IFinderRepairService
{
    /// <summary>
    /// Where the library lives - the root the repair writes into.
    /// </summary>
    /// <remarks>
    /// Resolved the way the pass resolves it, so the repair writes where the pass read
    /// from. The confirmation window names it, so it is resolved before the run starts and
    /// then kept.
    /// </remarks>
    Task<string> GetLibraryRootAsync(CancellationToken ct = default);

    /// <summary>
    /// Recovers the rows it is given, and reports what it did to each.
    /// </summary>
    /// <param name="rows">
    /// The rows to act on - the filtered set, already narrowed to the verdicts that have a
    /// mechanical path.
    /// </param>
    /// <param name="progress">How far the run has got.</param>
    /// <param name="ct">Stops the run. Whatever was already written stays written.</param>
    Task<FinderRepairSummary> RunAsync(
        IReadOnlyList<FinderRow> rows,
        IProgress<FinderRepairProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// How far the repair has got.
/// </summary>
public sealed record FinderRepairProgress(string Stage, int Done, int Total, string Detail);

/// <summary>
/// The repairer: it restores the parents and regenerates the children.
/// </summary>
/// <remarks>
/// <para>
/// The work is per pair, and one pair's failure does not stop the others. A pair is
/// restored before it is regenerated, because the child is derived from the parent.
/// </para>
/// <para>
/// The library root is resolved the way the pass resolves it, so the repair writes where
/// the pass read from. The database is never written.
/// </para>
/// </remarks>
public sealed class FinderRepairService(
    IMlDataService ml,
    IFinderLibraryService library,
    IDropBoxDataService dropBox,
    IFinderRepairLogProvider logs,
    IOptions<ServerOptions> options,
    ILogger<FinderRepairService> logger) : IFinderRepairService
{
    /// <summary>The quality used when the settings carry none.</summary>
    private const int DefaultQuality = 35;

    /// <summary>
    /// How often the progress is reported, in records.
    /// </summary>
    /// <remarks>
    /// A hundredth of the run, so the bar moves often enough to look alive whether the run
    /// is twenty records or a quarter of a million. At least one, so a short run reports
    /// every record.
    /// </remarks>
    private static int Step(int count) => Math.Max(1, count / 100);

    /// <summary>
    /// The resolved library root, kept so it is resolved once.
    /// </summary>
    private string? _libraryRoot;

    /// <summary>
    /// Where the library lives - the root the repair writes into.
    /// </summary>
    public async Task<string> GetLibraryRootAsync(CancellationToken ct = default)
    {
        if (_libraryRoot is not null)
        {
            return _libraryRoot;
        }

        var settings = await ml.GetSettingsAsync(ct);

        var configuredRoot = settings?.MediaLibraryLocalPath;
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            throw new InvalidOperationException(
                "ml.Settings.MediaLibraryLocalPath is not configured - the library root is unknown.");
        }

        // The configured root is the starting point, not the answer: the library may sit
        // directly under it, or under a folder named after the network share. It is
        // resolved against the folders the media rows name - the same call the pass makes.
        var mediaFolders = await ml.GetMediaFoldersAsync(ct);

        _libraryRoot = FinderLibraryRoot.Resolve(
            configuredRoot, settings?.MediaLibraryUncPath, mediaFolders);

        return _libraryRoot;
    }

    public async Task<FinderRepairSummary> RunAsync(
        IReadOnlyList<FinderRow> rows,
        IProgress<FinderRepairProgress>? progress = null,
        CancellationToken ct = default)
    {
        // The moment the procedure started. It names the log's folder and file (§10.5.1),
        // so it is taken HERE - before the settings and the library root are read - and
        // not when the log opens, which is later and can be on the next day.
        var startedAt = DateTimeOffset.Now;

        var settings = await ml.GetSettingsAsync(ct);

        var libraryRoot = await GetLibraryRootAsync(ct);

        var quality = settings?.VCatQualityPercent is { } q && q > 0 ? q : DefaultQuality;
        var thumbnailQuality = settings?.VCatThumbnailQualityPercent is { } tq && tq > 0 ? tq : DefaultQuality;
        var vcatWidth = settings?.VCatWidthInPixels ?? 1500;
        var thumbnailWidth = settings?.VCatThumbnailWidthInPixels ?? 300;

        var subject = rows
            .Where(r => r.Verdict.Id is 1 or 2 or 3)
            .ToList();

        var summary = new FinderRepairSummary
        {
            Shown = rows.Count,
            Recoverable = subject.Count,
            Recovered = 0,
            ParentRestored = 0,
            ChildRegenerated = 0,
            Both = 0,
            NotRecovered = rows.Count - subject.Count,
            Failed = 0,
            DownloadedBytes = 0,
            Replaced = 0,
            Folders = subject.Select(r => r.ParentFolder).Distinct().Count(),
        };

        // The log opens with the run's summary — the same figures the confirmation window
        // showed, so the two agree (§10.5.4). It goes in the folder named for the date the
        // recovery started.
        var log = await logs.OpenAsync(summary, startedAt, ct);
        summary = summary with { LogPath = log?.Path };

        var done = 0;

        // The width is how many records are recovered at the same time (§10.6). It is 1 -
        // the sequential recoverer - and it does not change WHAT is written, only how long
        // it takes.
        var width = Math.Max(1, options.Value.RecoverWidth);

        try
        {
            if (width == 1)
            {
                foreach (var row in subject)
                {
                    ct.ThrowIfCancellationRequested();

                    var record = await RepairOneAsync(
                        row, libraryRoot, quality, thumbnailQuality, vcatWidth, thumbnailWidth, ct);

                    if (log is not null)
                    {
                        await log.WriteAsync(record, ct);
                    }

                    summary = Accumulate(summary, record);
                    done++;

                    // The bar moves often enough to look alive: every step of a small run,
                    // and a hundredth of a large one.
                    if (done % Step(subject.Count) == 0 || done == subject.Count)
                    {
                        progress?.Report(new FinderRepairProgress(
                            "Recovering", done, subject.Count, $"{done} of {subject.Count}"));
                    }
                }
            }
            else
            {
                using var gate = new SemaphoreSlim(width);

                var tasks = subject.Select(async row =>
                {
                    await gate.WaitAsync(ct);

                    try
                    {
                        var record = await RepairOneAsync(
                            row, libraryRoot, quality, thumbnailQuality, vcatWidth, thumbnailWidth, ct);

                        if (log is not null)
                        {
                            await log.WriteAsync(record, ct);
                        }

                        var n = Interlocked.Increment(ref done);

                        if (n % Step(subject.Count) == 0 || n == subject.Count)
                        {
                            progress?.Report(new FinderRepairProgress(
                                "Recovering", n, subject.Count, $"{n} of {subject.Count}"));
                        }

                        return record;
                    }
                    finally
                    {
                        gate.Release();
                    }
                });

                var records = await Task.WhenAll(tasks);

                foreach (var record in records)
                {
                    summary = Accumulate(summary, record);
                }
            }
        }
        finally
        {
            if (log is not null)
            {
                await log.WriteSummaryAsync(summary, ct);
                await log.DisposeAsync();
            }
        }

        progress?.Report(new FinderRepairProgress(
            "Done", subject.Count, subject.Count, $"{summary.Recovered} recovered"));

        return summary;
    }

    /// <summary>
    /// Recovers one pair: the parent first, then the child from it.
    /// </summary>
    private async Task<FinderRepairRecord> RepairOneAsync(
        FinderRow row,
        string libraryRoot,
        int quality,
        int thumbnailQuality,
        int vcatWidth,
        int thumbnailWidth,
        CancellationToken ct)
    {
        var candidate = row.BestCandidate;

        var record = new FinderRepairRecord
        {
            ParentId = row.ParentId,
            ChildId = row.ChildId,
            ParentName = row.ParentFileName,
            Folder = row.ParentFolder,
            NodeId = candidate?.NodeId ?? 0,
            DropboxId = candidate?.DropboxId,
            NamespaceId = candidate?.NamespaceId,
            ContentHash = candidate?.ContentHash,
            Source = candidate?.Path,
            Recovered = false,
        };

        var parentPath = Path.Combine(libraryRoot, row.ParentFolder, row.ParentId.ToString().ToUpperInvariant());

        try
        {
            // ---- the parent ----
            if (row.Verdict.Id is 1 or 3)
            {
                if (candidate is null)
                {
                    return record with { Reason = "no candidate to restore the parent from" };
                }

                if (string.IsNullOrWhiteSpace(candidate.NamespaceId))
                {
                    return record with { Reason = "the candidate carries no namespace" };
                }

                Directory.CreateDirectory(Path.GetDirectoryName(parentPath)!);

                // Downloaded to a temporary name in the same directory and then moved into
                // place: a download cut short leaves a truncated file, and a truncated file
                // at the parent's path is CORRUPTED, which is worse than missing.
                var temporaryPath = parentPath + ".download";

                try
                {
                    await dropBox.DownloadAsync(
                        candidate.Path, temporaryPath, candidate.NamespaceId, ct);

                    File.Move(temporaryPath, parentPath, overwrite: true);
                }
                catch
                {
                    TryDelete(temporaryPath);
                    throw;
                }

                record = record with
                {
                    CopiedTo = parentPath,
                    CopiedBytes = new FileInfo(parentPath).Length,
                };
            }

            // ---- the child ----
            if (row.Verdict.Id is 2 or 3)
            {
                if (!File.Exists(parentPath))
                {
                    return record with { Reason = "the parent is not there to regenerate the child from" };
                }

                // The child's own recorded dimensions are the target. A child that records
                // none is sized from the settings instead (§10.8).
                var isThumbnail = row.ChildCategoryName.Equals(
                    "Thumbnail", StringComparison.OrdinalIgnoreCase);

                var target = row.ChildWidth.HasValue && row.ChildHeight.HasValue
                    ? Math.Max(row.ChildWidth.Value, row.ChildHeight.Value)
                    : (isThumbnail ? thumbnailWidth : vcatWidth);

                var childQuality = isThumbnail ? thumbnailQuality : quality;

                var childPath = Path.Combine(
                    libraryRoot, row.ParentFolder, row.ChildId.ToString().ToUpperInvariant());

                Directory.CreateDirectory(Path.GetDirectoryName(childPath)!);

                await WriteChildAsync(parentPath, childPath, target, childQuality, ct);

                record = record with
                {
                    ResizedFor = row.ChildId,
                    ResizedFrom = parentPath,
                    ResizedTo = childPath,
                    Target = target,
                    Result = Describe(childPath),
                };
            }

            return record with { Recovered = true };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Recovery failed for {ParentId}.", row.ParentId);
            return record with { Reason = ex.Message };
        }
    }

    /// <summary>
    /// Regenerates the child from the parent: the longer side scaled to the target, the
    /// aspect ratio kept, CMYK converted to RGB, written as JPEG.
    /// </summary>
    private static async Task WriteChildAsync(
        string parentPath,
        string childPath,
        int target,
        int quality,
        CancellationToken ct)
    {
        using var image = new MagickImage();

        await image.ReadAsync(parentPath, ct);

        // The box is square and Magick does the fit - the importer's own shape.
        // IgnoreAspectRatio = false fits the image inside the box with its aspect ratio
        // kept, so the longer side lands on the box's side. Greater = true makes the fit a
        // shrink only: a parent smaller than the target keeps its own size.
        //
        // The box's side is uint: the target is a pixel count and is never negative.
        var side = (uint)Math.Max(1, target);

        var size = new MagickGeometry(side, side)
        {
            IgnoreAspectRatio = false,
            Greater = true
        };

        image.Resize(size);

        image.Quality = (uint)quality;

        if (image.ColorSpace == ColorSpace.CMYK)
        {
            image.ColorSpace = ColorSpace.RGB;
        }

        // The child's on-disk name is a bare GUID with no extension, so the format has to
        // be given to Magick explicitly.
        await image.WriteAsync(childPath, MagickFormat.Jpeg, ct);
    }

    /// <summary>
    /// What the file is now: its length and its dimensions.
    /// </summary>
    private static string Describe(string path)
    {
        try
        {
            var length = new FileInfo(path).Length;

            using var image = new MagickImage();
            image.Read(path);

            return $"size {length:N0}, {image.Width} × {image.Height}";
        }
        catch (Exception)
        {
            return $"size {new FileInfo(path).Length:N0}";
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // A temporary file that cannot be deleted is not worth failing the pair over.
        }
    }

    /// <summary>
    /// Folds one pair's outcome into the run's totals.
    /// </summary>
    private static FinderRepairSummary Accumulate(FinderRepairSummary summary, FinderRepairRecord record)
    {
        var parentRestored = record.CopiedTo is not null;
        var childRegenerated = record.ResizedTo is not null;

        if (!record.Recovered)
        {
            return summary with { Failed = summary.Failed + 1 };
        }

        return summary with
        {
            Recovered = summary.Recovered + 1,
            ParentRestored = summary.ParentRestored + (parentRestored ? 1 : 0),
            ChildRegenerated = summary.ChildRegenerated + (childRegenerated ? 1 : 0),
            Both = summary.Both + (parentRestored && childRegenerated ? 1 : 0),
            DownloadedBytes = summary.DownloadedBytes + record.CopiedBytes,
            Replaced = summary.Replaced + (parentRestored ? 1 : 0) + (childRegenerated ? 1 : 0),
        };
    }
}
