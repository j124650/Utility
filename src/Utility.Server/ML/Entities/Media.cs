using System.ComponentModel.DataAnnotations.Schema;

namespace Utility.Server.ML.Entities;

/// <summary>Read-only projection of <c>ml.media</c> (765k+ rows).</summary>
[Table("media")]
public class Media
{
    public Guid Id { get; set; }

    public long? Size { get; set; }

    public int? HeightInPixels { get; set; }

    public int? WidthInPixels { get; set; }

    public int? Resolution { get; set; }

    public string? FileName { get; set; }

    public string? FileExtension { get; set; }

    public DateTime? DateAdded { get; set; }

    public Guid? OriginalMediaId { get; set; }

    public short? MediaTypeId { get; set; }

    public short? MediaCategoryId { get; set; }

    public short? MediaSubCategoryId { get; set; }

    public string? ExternalKey { get; set; }

    /// <summary>nvarchar(max) in the database — kept as string, not read eagerly by default.</summary>
    public string? Path { get; set; }

    public bool? IsCad { get; set; }
}

[Table("MediaType")]
public class MediaType
{
    public short Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

[Table("MediaCategory")]
public class MediaCategory
{
    public short Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int SystemId { get; set; }
}

[Table("MediaSubCategory")]
public class MediaSubCategory
{
    public short Id { get; set; }

    public string? Name { get; set; }

    public short? SortOrder { get; set; }
}

[Table("MediaSubType")]
public class MediaSubType
{
    public short Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public short? SortOrder { get; set; }
}