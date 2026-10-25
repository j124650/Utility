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
    /// <summary>Whether the file is present and usable - ok, missing or corrupted (§6).</summary>
    public required FileState State { get; init; }

    /// <summary>The row's recorded <c>Size</c> - the length of the file as it was imported.</summary>
    public required long? Size { get; init; }

    /// <summary>
    /// Whether the file's length on disk matches the recorded size - match or differs.
    /// Null when the file is missing: there is nothing on disk to compare (§6).
    /// </summary>
    public required SizeCheck? SizeCheck { get; init; }

    /// <summary>
    /// The row's recorded dimensions, as §6 words them: <c>WidthInPixels × HeightInPixels</c>.
    /// Empty when the row records none.
    /// </summary>
    public required string Dimensions { get; init; }

    /// <summary>Why the file would not open. Set only when the state is corrupted.</summary>
    public string? Fault { get; init; }

    /// <summary>True when the file is present and opens.</summary>
    public bool IsOk => State == FileState.Ok;

    /// <summary>The size in bytes, as the reader sees it. Empty when the row records none.</summary>
    public string SizeDisplay => Size.HasValue ? Size.Value.ToString("N0") : string.Empty;

    /// <summary>Builds the dimensions text from a row's recorded pixel size.</summary>
    public static string FormatDimensions(int? width, int? height) =>
        width.HasValue && height.HasValue ? $"{width} × {height}" : string.Empty;
}

/// <summary>
/// The three states a file can be in (§6).
/// </summary>
/// <remarks>
/// A value rather than an enum, because the three words are what the reader sees and a coined
/// label would have to be learned at the moment the reader is scanning the table. It carries
/// its own reading, so the grid binds the field itself and gets "ok", "missing" or
/// "corrupted" with nothing re-derived in the UI.
/// </remarks>
public readonly record struct FileState(int Id, string Text) : IComparable<FileState>
{
    /// <summary>The file is at its path and opens as an image.</summary>
    public static readonly FileState Ok = new(0, "ok");

    /// <summary>The file is not at its path.</summary>
    public static readonly FileState Missing = new(1, "missing");

    /// <summary>The file is at its path but does not open as an image.</summary>
    public static readonly FileState Corrupted = new(2, "corrupted");

    /// <summary>The three states, in the order of §6's table.</summary>
    public static readonly IReadOnlyList<FileState> All = [Ok, Missing, Corrupted];

    public int CompareTo(FileState other) => Id.CompareTo(other.Id);

    public override string ToString() => Text;
}

/// <summary>
/// Whether a file's length on disk matches the size recorded for it (§6).
/// </summary>
public readonly record struct SizeCheck(int Id, string Text) : IComparable<SizeCheck>
{
    /// <summary>The length matches the recorded size.</summary>
    public static readonly SizeCheck Match = new(0, "match");

    /// <summary>The length does not match - the file is not the one that was imported.</summary>
    public static readonly SizeCheck Differs = new(1, "differs");

    /// <summary>The two checks, in the order of §6's table.</summary>
    public static readonly IReadOnlyList<SizeCheck> All = [Match, Differs];

    public int CompareTo(SizeCheck other) => Id.CompareTo(other.Id);

    public override string ToString() => Text;
}
