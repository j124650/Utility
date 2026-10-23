namespace Utility.Server.ML.Services;

/// <summary>
/// Parameter object for <see cref="IMlDataService.GetMediaAsync"/>.
/// Kept as a record so new filter criteria can be added without breaking callers.
/// </summary>
public sealed record MediaQuery
{
    /// <summary>0-based page number.</summary>
    public int PageIndex { get; init; }

    public int PageSize { get; init; } = 100;

    public short? MediaTypeId { get; init; }

    public short? MediaCategoryId { get; init; }

    public short? MediaSubCategoryId { get; init; }

    public Guid? MediaId { get; init; }

    public string? ExternalKey { get; init; }

    public string? FileNameContains { get; init; }

    public DateTime? AddedOnOrAfter { get; init; }

    public long? MinSizeBytes { get; init; }

    /// <summary>Field to order by, mapped to real columns. Ignored when null.</summary>
    public MediaSortField SortBy { get; init; } = MediaSortField.Id;

    public bool Descending { get; init; }

    public int NormalizedPageSize => Math.Clamp(PageSize, 1, 1000);

    public int NormalizedPageIndex => Math.Max(PageIndex, 0);
}

public enum MediaSortField
{
    Id = 0,
    DateAdded = 1,
    Size = 2,
    FileName = 3
}