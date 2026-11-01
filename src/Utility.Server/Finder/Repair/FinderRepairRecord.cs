using Utility.Server.Finder;

namespace Utility.Server.Finder.Repair;

/// <summary>
/// What the repair did to one pair.
/// </summary>
/// <remarks>
/// The record of the run, per pair (§10.5.2). Its fields are the workflow's own steps:
/// the ids each step used, what was copied and where, what was resized on behalf of
/// which id, and what the file became.
/// </remarks>
public sealed record FinderRepairRecord
{
    /// <summary>The parent's <c>ml.media.Id</c> — the GUID, and the file's name on disk.</summary>
    public required Guid ParentId { get; init; }

    /// <summary>The child's <c>ml.media.Id</c> — the GUID the child's file is named by.</summary>
    public required Guid ChildId { get; init; }

    /// <summary><c>ml.media.FileName</c> — the name the candidate was matched on.</summary>
    public required string ParentName { get; init; }

    /// <summary><c>ml.media.Path</c> — the folder both files sit in.</summary>
    public required string Folder { get; init; }

    /// <summary>The candidate's <c>ml.DropBoxNode.Id</c> — the index row the bytes came from.</summary>
    public long NodeId { get; init; }

    /// <summary>The candidate's <c>ml.DropBoxNode.DropboxId</c> — Dropbox's object id.</summary>
    public string? DropboxId { get; init; }

    /// <summary>The candidate's <c>ml.DropBoxNode.NamespaceId</c> — the scope it was fetched in.</summary>
    public string? NamespaceId { get; init; }

    /// <summary>The candidate's <c>ml.DropBoxNode.ContentHash</c> — Dropbox's own hash of the bytes.</summary>
    public string? ContentHash { get; init; }

    /// <summary>The candidate's full Dropbox path — where the bytes came from.</summary>
    public string? Source { get; init; }

    /// <summary>The parent's full path on disk, where the bytes were written.</summary>
    public string? CopiedTo { get; init; }

    /// <summary>How many bytes were written.</summary>
    public long CopiedBytes { get; init; }

    /// <summary>The child's id the resize was done on behalf of.</summary>
    public Guid? ResizedFor { get; init; }

    /// <summary>The parent's full path on disk — the file the resize read.</summary>
    public string? ResizedFrom { get; init; }

    /// <summary>The child's full path on disk, where the result was written.</summary>
    public string? ResizedTo { get; init; }

    /// <summary>The size the resize aimed at — the child's recorded dimensions.</summary>
    public int? Target { get; init; }

    /// <summary>What the file is now — its length, and its dimensions.</summary>
    public string? Result { get; init; }

    /// <summary>Why the pair was not recovered.</summary>
    public string? Reason { get; init; }

    /// <summary>Whether the pair was recovered.</summary>
    public required bool Recovered { get; init; }

    /// <summary>
    /// The operation's number within the run - the first is 1.
    /// </summary>
    /// <remarks>
    /// It is what ties an operation's events together in the log (§10.5.3). The log is
    /// written as the work happens, so an operation's events are separate lines, and the
    /// number is what says which operation a line belongs to.
    /// </remarks>
    public int Number { get; init; }

    /// <summary>
    /// What the run set out to do to this pair - "restore the parent", "regenerate the
    /// child", or both.
    /// </summary>
    /// <remarks>
    /// It is the PLAN, and it is known before any work happens. The outcome is a
    /// different thing, and it is written when the operation ends.
    /// </remarks>
    public string Plan { get; init; } = string.Empty;

    /// <summary>When the operation started.</summary>
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>When the operation ended, or null while it is still running.</summary>
    public DateTimeOffset? EndedAt { get; init; }
}

/// <summary>
/// What the repair did to the set it was given.
/// </summary>
public sealed record FinderRepairSummary
{
    /// <summary>The rows the filters left — the size of the set the repair was asked about.</summary>
    public required int Shown { get; init; }

    /// <summary>How many of those were recoverable.</summary>
    public required int Recoverable { get; init; }

    /// <summary>How many were actually recovered.</summary>
    public required int Recovered { get; init; }

    /// <summary>How many parents were restored from Dropbox.</summary>
    public required int ParentRestored { get; init; }

    /// <summary>How many children were regenerated from their parent.</summary>
    public required int ChildRegenerated { get; init; }

    /// <summary>How many pairs were restored and then regenerated.</summary>
    public required int Both { get; init; }

    /// <summary>How many pairs were not recovered.</summary>
    public required int NotRecovered { get; init; }

    /// <summary>How many pairs failed.</summary>
    public required int Failed { get; init; }

    /// <summary>How many bytes were downloaded.</summary>
    public required long DownloadedBytes { get; init; }

    /// <summary>How many existing files were replaced.</summary>
    public required int Replaced { get; init; }

    /// <summary>How many folders were written to.</summary>
    public required int Folders { get; init; }

    /// <summary>Where the log was written, or null when it could not be opened.</summary>
    public string? LogPath { get; init; }
}
