using ImageMagick;

namespace Utility.Server.Finder.Services;

/// <summary>
/// Reads the media library on disk: whether each file is there, and whether it opens.
/// </summary>
/// <remarks>
/// The library root comes from <c>ml.Settings</c> and is read at run time, so the path is
/// configuration rather than a constant.
/// </remarks>
public interface IFinderLibraryService
{
    /// <summary>The library root, or null when it is not configured.</summary>
    string? LibraryRoot { get; }

    /// <summary>Reads one file's state.</summary>
    Task<FinderFileState> GetFileStateAsync(
        string libraryRoot,
        string? folder,
        Guid id,
        long? recordedSize,
        int? recordedWidth,
        int? recordedHeight,
        CancellationToken ct = default);
}

/// <summary>Reads file states from the media library.</summary>
public sealed class FinderLibraryService : IFinderLibraryService
{
    public string? LibraryRoot { get; private set; }

    /// <summary>
    /// Builds the on-disk path of a media row's file.
    /// <para>
    /// The file is named by the row's own GUID, with dashes and no extension, and sits in
    /// the row's folder. The same rule applies to a parent and to a child.
    /// </para>
    /// </summary>
    public static string BuildPath(string libraryRoot, string? folder, Guid id)
    {
        var relative = string.IsNullOrWhiteSpace(folder) ? string.Empty : folder;
        return Path.Combine(libraryRoot, relative, id.ToString());
    }

    public async Task<FinderFileState> GetFileStateAsync(
        string libraryRoot,
        string? folder,
        Guid id,
        long? recordedSize,
        int? recordedWidth,
        int? recordedHeight,
        CancellationToken ct = default)
    {
        var path = BuildPath(libraryRoot, folder, id);

        if (!File.Exists(path))
        {
            return new FinderFileState
            {
                State = FileState.Missing,
                Size = recordedSize,
                SizeCheck = null,
                Dimensions = FinderFileState.FormatDimensions(recordedWidth, recordedHeight)
            };
        }

        long length;
        try
        {
            length = new FileInfo(path).Length;
        }
        catch (Exception ex)
        {
            // The file vanished or is locked between the existence check and the read.
            // Treat it as missing rather than reporting a state we cannot establish.
            return new FinderFileState
            {
                State = FileState.Missing,
                Size = recordedSize,
                SizeCheck = null,
                Dimensions = FinderFileState.FormatDimensions(recordedWidth, recordedHeight),
                Fault = ex.Message
            };
        }

        var sizeCheck = recordedSize.HasValue && length == recordedSize.Value
            ? SizeCheck.Match
            : SizeCheck.Differs;

        var (state, fault) = await OpenAsync(path, ct);

        return new FinderFileState
        {
            State = state,
            Size = recordedSize,
            SizeCheck = sizeCheck,
            Dimensions = FinderFileState.FormatDimensions(recordedWidth, recordedHeight),
            Fault = fault
        };
    }

    /// <summary>
    /// Opens the image and reports whether it decoded. A real decode, not a magic-byte sniff:
    /// a truncated file is corrupted, not ok.
    /// </summary>
    private static async Task<(FileState State, string? Fault)> OpenAsync(string path, CancellationToken ct)
    {
        try
        {
            using var image = new MagickImage();
            await image.ReadAsync(path, ct);
            return (FileState.Ok, null);
        }
        catch (Exception ex)
        {
            return (FileState.Corrupted, ex.Message);
        }
    }
}
