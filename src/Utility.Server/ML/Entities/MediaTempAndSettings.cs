using System.ComponentModel.DataAnnotations.Schema;

namespace Utility.Server.ML.Entities;

/// <summary>Scratch table used by the catalog path import; empty in this database.</summary>
[Table("MediaTempCatalogPaths")]
public class MediaTempCatalogPaths
{
    public int Id { get; set; }

    public string? FileFullPathName { get; set; }
}

/// <summary>Scratch table of file names; ~289k rows.</summary>
[Table("MediaTempFilesNames")]
public class MediaTempFilesNames
{
    public int Id { get; set; }

    /// <summary>Column is literally named <c>MediaId_X</c>.</summary>
    [Column("MediaId_X")]
    public Guid? MediaIdX { get; set; }

    public string? FileFullPathName { get; set; }

    /// <summary>Column is literally named <c>MediaId_2</c> and is a varchar, not a guid.</summary>
    [Column("MediaId_2")]
    public string? MediaId2 { get; set; }
}

/// <summary>
/// <c>ml.Settings</c> is a keyless singleton table (single row, no primary key).
/// </summary>
[Table("Settings")]
public class MlSettings
{
    public string? MediaLibraryLocalPath { get; set; }

    public string? MediaLibraryUncPath { get; set; }

    public string? PhotoLabInboxVcatUncPath { get; set; }

    public string? PhotoLabInboxSalesUncPath { get; set; }

    public string? PhotoLabErrorsUncPath { get; set; }

    public int? VCatWidthInPixels { get; set; }

    public int? VCatQualityPercent { get; set; }

    public string? MediaLibraryUrl { get; set; }

    public string? PhotoLabArchiveUncPath { get; set; }

    public int? VCatThumbnailWidthInPixels { get; set; }

    public int? VCatThumbnailQualityPercent { get; set; }

    public string? PreliminaryInboxVcatUncPath { get; set; }

    public string? ExportLocalPath { get; set; }

    public string? ExportUncPath { get; set; }

    public string? ExportServiceExeFullPath { get; set; }

    public string? ExportUrl { get; set; }

    public string? ImportServiceExeFullPath { get; set; }

    public DateTime? ImportServiceStarted { get; set; }

    public int? ExportExpiresHours { get; set; }

    public int SystemId { get; set; }

    public int UserRoleId { get; set; }

    public int ManagerRoleId { get; set; }

    public string? PhotoLabInboxNoValidationVcatUncPath { get; set; }

    public string? PhotoLabInboxCadsUncPath { get; set; }

    public string? PhotoLabInboxCadstUncPath { get; set; }
}