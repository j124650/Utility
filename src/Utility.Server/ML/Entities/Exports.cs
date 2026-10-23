using System.ComponentModel.DataAnnotations.Schema;

namespace Utility.Server.ML.Entities;

[Table("ExportStatus")]
public class ExportStatus
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

[Table("ExportTemplate")]
public class ExportTemplate
{
    public int Id { get; set; }

    public int ExternalUserId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>smalldatetime in the database.</summary>
    public DateTime Created { get; set; }

    public string? Preset { get; set; }
}

[Table("ExportHeader")]
public class ExportHeader
{
    public int Id { get; set; }

    /// <summary>smalldatetime in the database.</summary>
    public DateTime CreateDate { get; set; }

    public int ExportStatusId { get; set; }

    public int ExternalUserId { get; set; }

    public string ArchiveName { get; set; } = string.Empty;

    public bool? IsNotificationSent { get; set; }

    public string? Result { get; set; }

    /// <summary>Maps to the DESC column (a reserved word).</summary>
    [Column("Desc")]
    public string? Description { get; set; }
}

[Table("ExportDetail")]
public class ExportDetail
{
    public int Id { get; set; }

    public int ExportHeaderId { get; set; }

    public Guid? ProductVariantId { get; set; }

    public Guid MediaId { get; set; }

    public string? SourcePath { get; set; }

    public string? DestinationPath { get; set; }
}