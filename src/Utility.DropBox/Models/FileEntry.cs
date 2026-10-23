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
    bool IsDownloadable)
{
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
                IsDownloadable: file.IsDownloadable);
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