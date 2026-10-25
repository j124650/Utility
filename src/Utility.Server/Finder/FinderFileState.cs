namespace Utility.Server.Finder;

/// <summary>
/// The state of one file on disk, relative to its media row.
/// </summary>
/// <remarks>
/// The same five things are carried for both sides of a pair - the parent file and the child
/// file - so every place that shows a file's data shows these.
/// </remarks>
public sealed record FinderFileState
{
    /// <summary>Whether the file is present and usable.</summary>
    public required FileState State { get; init; }

    /// <summary>The row's recorded <c>Size</c>.</summary>
    public long? Size { get; init; }

    /// <summary>
    /// Whether the file's length on disk matches the recorded size. Null when the file is
    /// missing - there is nothing on disk to compare.
    /// </summary>
    public SizeCheck? SizeCheck { get; init; }

    /// <summary>The row's recorded dimensions.</summary>
    public int? Width { get; init; }

    /// <summary>The row's recorded dimensions.</summary>
    public int? Height { get; init; }

    /// <summary>Why the file would not open. Set only when the state is corrupted.</summary>
    public string? Fault { get; init; }

    public string DimensionsDisplay =>
        Width.HasValue && Height.HasValue ? $"{Width} × {Height}" : string.Empty;

    public string SizeDisplay => Size.HasValue ? Size.Value.ToString("N0") : string.Empty;

    /// <summary>True when the file is present and opens.</summary>
    public bool IsOk => State == FileState.Ok;
}

/// <summary>The three states a file can be in.</summary>
public enum FileState
{
    /// <summary>The file is at its path and opens as an image.</summary>
    Ok = 0,

    /// <summary>The file is not at its path.</summary>
    Missing = 1,

    /// <summary>The file is at its path but does not open as an image.</summary>
    Corrupted = 2
}

/// <summary>Whether a file's length on disk matches the size recorded for it.</summary>
public enum SizeCheck
{
    /// <summary>The length matches the recorded size.</summary>
    Match = 0,

    /// <summary>The length does not match - the file is not the one that was imported.</summary>
    Differs = 1
}
