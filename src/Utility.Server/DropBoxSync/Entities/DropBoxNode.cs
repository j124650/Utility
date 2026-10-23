using System.ComponentModel.DataAnnotations.Schema;

namespace Utility.Server.DropBoxSync.Entities;

/// <summary>
/// One node of the synced Dropbox hierarchy, mapped to <c>ml.DropBoxNode</c>.
/// <para>
/// This is a writable projection of the table this app owns. It is deliberately
/// separate from <c>MlDbContext</c>: that context maps the <c>ml.</c> tables another
/// system owns and throws on every SaveChanges, so it must never reach this entity.
/// </para>
/// </summary>
[Table("DropBoxNode")]
public sealed class DropBoxNode
{
    public long Id { get; set; }

    /// <summary>Namespace the node lives in: <c>personal</c>, or a team namespace id.</summary>
    public string NamespaceId { get; set; } = string.Empty;

    /// <summary>
    /// Full path exactly as Dropbox reports it. Dropbox returns namespace-absolute
    /// paths regardless of where the walk started, so this is never reconstructed.
    /// </summary>
    public string PathDisplay { get; set; } = string.Empty;

    /// <summary>Dropbox's own canonical lowercase path; the comparison key.</summary>
    public string PathLower { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 over NamespaceId + '|' + PathLower, computed by the database as a
    /// PERSISTED column. Never assigned by this code: it exists only so the natural
    /// key fits inside SQL Server's 1700-byte index-key limit.
    /// </summary>
    public byte[]? PathHash { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsFolder { get; set; }

    /// <summary>Parent node's surrogate id. Null only for the start folder of a run.</summary>
    public long? ParentId { get; set; }

    /// <summary>Dropbox's own object id. Null for folders this walk did not expand.</summary>
    public string? DropboxId { get; set; }

    /// <summary>Null for a folder; 0 for a genuinely empty file. Do not conflate them.</summary>
    public long? SizeBytes { get; set; }

    public string? Rev { get; set; }

    public string? ContentHash { get; set; }

    /// <summary>
    /// Image or video width in pixels. Null unless the sync requested media info AND
    /// Dropbox returned real dimensions.
    /// </summary>
    public int? WidthPixels { get; set; }

    /// <summary>Image or video height in pixels. See <see cref="WidthPixels"/>.</summary>
    public int? HeightPixels { get; set; }

    /// <summary>
    /// What Dropbox said about this file's media nature, and the reason the two width and
    /// height columns cannot speak for themselves:
    /// <list type="bullet">
    /// <item><description><c>null</c> - not an image or video, so never has dimensions.</description></item>
    /// <item><description><c>pending</c> - Dropbox deferred it; a later read may succeed.</description></item>
    /// <item><description><c>photo</c> / <c>video</c> - dimensions are authoritative.</description></item>
    /// </list>
    /// </summary>
    public string? MediaInfoTag { get; set; }

    public DateTimeOffset? ServerModified { get; set; }

    public DateTimeOffset? ClientModified { get; set; }

    public bool? IsDownloadable { get; set; }

    public string? SharedFolderId { get; set; }

    /// <summary>
    /// Per-folder sync outcome for the current run; null for files.
    /// <para>
    /// This is what makes the sweep safe: only <c>Ok</c> may have stale rows deleted
    /// beneath it. A skipped or failed folder was never fully listed, so its children
    /// look stale while being perfectly valid.
    /// </para>
    /// </summary>
    public string? SyncStatus { get; set; }

    /// <summary>Short diagnostic for a non-Ok status, e.g. the error text.</summary>
    public string? SyncStatusReason { get; set; }

    /// <summary>
    /// Identifies the run that last saw this row. The sweep deletes rows whose
    /// SyncRunId differs from the current run, which gives exact set membership
    /// without depending on clock skew.
    /// </summary>
    public Guid SyncRunId { get; set; }

    public DateTimeOffset SyncedUtc { get; set; }
}