using Dropbox.Api.Files;

namespace Utility.DropBox.Models;

/// <summary>
/// A single Dropbox entry flattened for UI binding — folders and files share one shape
/// so a DataGrid can render a mixed listing without per-row templates.
/// </summary>
public sealed record FileEntry(
    string Name,
    string PathDisplay,
    string PathLower,
    bool IsFolder,
    long SizeBytes,
    string? Id,
    string? Rev,
    string? ContentHash,
    DateTimeOffset ServerModified,
    DateTimeOffset ClientModified,
    bool IsDownloadable,
    long? WidthPixels = null,
    long? HeightPixels = null,
    string? MediaKind = null)
{
    /// <summary>
    /// True when the file is an image or video AND Dropbox actually returned dimensions.
    /// <para>
    /// <see cref="MediaKind"/> is what makes this trustworthy: it is null for a file with
    /// no media info at all (a .txt), but 'pending' when Dropbox deferred the work. A
    /// pending file is NOT known to be dimension-less - it just needs asking again.
    /// </para>
    /// </summary>
    public bool HasDimensions => WidthPixels is not null && HeightPixels is not null;

    /// <summary>True when Dropbox deferred media info and a later read may succeed.</summary>
    public bool IsMediaInfoPending =>
        string.Equals(MediaKind, "pending", StringComparison.OrdinalIgnoreCase);
    public string SizeDisplay => IsFolder
        ? string.Empty
        : FormatSize(SizeBytes);

    /// <summary>
    /// Folders carry no timestamp in Dropbox metadata, and the SDK union has no folder
    /// variant for one — they come back as DateTimeOffset.MinValue. Render that as empty
    /// rather than leaking "0001-01-01" into the grid.
    /// </summary>
    public string ModifiedDisplay =>
        ServerModified == DateTimeOffset.MinValue
            ? string.Empty
            : ServerModified.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");

    public static FileEntry From(Metadata metadata)
    {
        // Stone union type: use the IsFile/IsFolder discriminators rather than casting.
        if (metadata.IsFolder)
        {
            var folder = metadata.AsFolder;
            return new FileEntry(
                folder.Name,
                folder.PathDisplay ?? folder.PathLower ?? folder.Name,
                folder.PathLower ?? folder.PathDisplay ?? folder.Name,
                IsFolder: true,
                SizeBytes: 0,
                Id: folder.Id,
                Rev: null,
                ContentHash: null,
                ServerModified: DateTimeOffset.MinValue,
                ClientModified: DateTimeOffset.MinValue,
                IsDownloadable: false);
        }

        if (metadata.IsFile)
        {
            var file = metadata.AsFile;

            // MediaInfo is a Stone union and is ONLY populated when the request asked for
            // it (include_media_info). Three outcomes matter, and conflating them would be
            // a silent correctness bug:
            //   null      - not an image/video (or not requested): no dimensions, ever
            //   'pending' - Dropbox deferred it; dimensions may be available on a re-read
            //   'photo'/'video' - dimensions are real
            long? width = null;
            long? height = null;
            string? mediaKind = null;

            if (file.MediaInfo is not null)
            {
                if (file.MediaInfo.IsPending)
                {
                    mediaKind = "pending";
                }
                else if (file.MediaInfo.IsMetadata)
                {
                    // MediaInfo.Metadata is a wrapper type: the real MediaMetadata sits on
                    // its Value property (verified by reflection, not guessed).
                    var media = file.MediaInfo.AsMetadata.Value;
                    mediaKind = media.IsPhoto ? "photo" : media.IsVideo ? "video" : null;

                    // Dimensions live on MediaMetadata itself, common to photo and video.
                    // Width/Height are UInt64 in the SDK.
                    if (media.Dimensions is not null)
                    {
                        width = (long)media.Dimensions.Width;
                        height = (long)media.Dimensions.Height;
                    }
                }
            }

            return new FileEntry(
                file.Name,
                file.PathDisplay ?? file.PathLower ?? file.Name,
                file.PathLower ?? file.PathDisplay ?? file.Name,
                IsFolder: false,
                SizeBytes: (long)file.Size,
                Id: file.Id,
                Rev: file.Rev,
                ContentHash: file.ContentHash,
                ServerModified: new DateTimeOffset(file.ServerModified, TimeSpan.Zero),
                ClientModified: new DateTimeOffset(file.ClientModified, TimeSpan.Zero),
                IsDownloadable: file.IsDownloadable,
                WidthPixels: width,
                HeightPixels: height,
                MediaKind: mediaKind);
        }

        if (metadata.IsDeleted)
        {
            var del = metadata.AsDeleted;
            return new FileEntry(
                del.Name,
                del.PathDisplay ?? del.PathLower ?? del.Name,
                del.PathLower ?? del.PathDisplay ?? del.Name,
                IsFolder: false,
                SizeBytes: 0,
                Id: null,
                Rev: null,
                ContentHash: null,
                ServerModified: DateTimeOffset.MinValue,
                ClientModified: DateTimeOffset.MinValue,
                IsDownloadable: false);
        }

        return new FileEntry(
            metadata.Name ?? "(unknown)",
            metadata.PathDisplay ?? string.Empty,
            metadata.PathLower ?? string.Empty,
            IsFolder: false, SizeBytes: 0, Id: null, Rev: null, ContentHash: null,
            ServerModified: DateTimeOffset.MinValue, ClientModified: DateTimeOffset.MinValue,
            IsDownloadable: false);
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }
}