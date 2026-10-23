using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Utility.Server.ML.Entities;

namespace Utility.Server.ML.Data;

/// <summary>
/// Model configuration for the read-only <c>ml.</c> schema.
/// Key differences from the writable schema:
/// <list type="bullet">
/// <item><description>Identity columns are marked <c>ValueGeneratedNever</c> — EF must never write.</description></item>
/// <item><description><c>ml.Settings</c> has no primary key, so it is mapped as keyless.</description></item>
/// <item><description>No keys, indexes or foreign keys are declared: the source schema declares none.</description></item>
/// </list>
/// </summary>
public class MlModelConfiguration : IEntityTypeConfiguration<Media>
{
    public void Configure(EntityTypeBuilder<Media> builder)
    {
        builder.ToTable("media", MlDbContext.Schema);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.Size).HasColumnName("Size");
        builder.Property(x => x.HeightInPixels).HasColumnName("HeightInPixels");
        builder.Property(x => x.WidthInPixels).HasColumnName("WidthInPixels");
        builder.Property(x => x.Resolution).HasColumnName("Resolution");
        builder.Property(x => x.FileName).HasColumnName("FileName");
        builder.Property(x => x.FileExtension).HasColumnName("FileExtension");
        builder.Property(x => x.DateAdded).HasColumnName("DateAdded");
        builder.Property(x => x.OriginalMediaId).HasColumnName("OriginalMediaId");
        builder.Property(x => x.MediaTypeId).HasColumnName("MediaTypeId");
        builder.Property(x => x.MediaCategoryId).HasColumnName("MediaCategoryId");
        builder.Property(x => x.MediaSubCategoryId).HasColumnName("MediaSubCategoryId");
        builder.Property(x => x.ExternalKey).HasColumnName("ExternalKey");
        builder.Property(x => x.Path).HasColumnName("Path");
        builder.Property(x => x.IsCad).HasColumnName("IsCad");
    }
}

public class MediaTypeConfiguration : IEntityTypeConfiguration<MediaType>
{
    public void Configure(EntityTypeBuilder<MediaType> builder)
    {
        builder.ToTable("MediaType", MlDbContext.Schema);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Name).HasColumnName("Name").IsRequired();
    }
}

public class MediaCategoryConfiguration : IEntityTypeConfiguration<MediaCategory>
{
    public void Configure(EntityTypeBuilder<MediaCategory> builder)
    {
        builder.ToTable("MediaCategory", MlDbContext.Schema);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Name).HasColumnName("Name").IsRequired();
        builder.Property(x => x.SystemId).HasColumnName("SystemId");
    }
}

public class MediaSubCategoryConfiguration : IEntityTypeConfiguration<MediaSubCategory>
{
    public void Configure(EntityTypeBuilder<MediaSubCategory> builder)
    {
        builder.ToTable("MediaSubCategory", MlDbContext.Schema);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Name).HasColumnName("Name");
        builder.Property(x => x.SortOrder).HasColumnName("SortOrder");
    }
}

public class MediaSubTypeConfiguration : IEntityTypeConfiguration<MediaSubType>
{
    public void Configure(EntityTypeBuilder<MediaSubType> builder)
    {
        builder.ToTable("MediaSubType", MlDbContext.Schema);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Name).HasColumnName("Name").IsRequired();
        builder.Property(x => x.SortOrder).HasColumnName("SortOrder");
    }
}

public class MediaTempCatalogPathsConfiguration : IEntityTypeConfiguration<MediaTempCatalogPaths>
{
    public void Configure(EntityTypeBuilder<MediaTempCatalogPaths> builder)
    {
        builder.ToTable("MediaTempCatalogPaths", MlDbContext.Schema);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.FileFullPathName).HasColumnName("FileFullPathName");
    }
}

public class MediaTempFilesNamesConfiguration : IEntityTypeConfiguration<MediaTempFilesNames>
{
    public void Configure(EntityTypeBuilder<MediaTempFilesNames> builder)
    {
        builder.ToTable("MediaTempFilesNames", MlDbContext.Schema);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.MediaIdX).HasColumnName("MediaId_X");
        builder.Property(x => x.FileFullPathName).HasColumnName("FileFullPathName");
        builder.Property(x => x.MediaId2).HasColumnName("MediaId_2");
    }
}

public class ExportStatusConfiguration : IEntityTypeConfiguration<ExportStatus>
{
    public void Configure(EntityTypeBuilder<ExportStatus> builder)
    {
        builder.ToTable("ExportStatus", MlDbContext.Schema);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Name).HasColumnName("Name").IsRequired();
    }
}

public class ExportTemplateConfiguration : IEntityTypeConfiguration<ExportTemplate>
{
    public void Configure(EntityTypeBuilder<ExportTemplate> builder)
    {
        builder.ToTable("ExportTemplate", MlDbContext.Schema);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.ExternalUserId).HasColumnName("ExternalUserId");
        builder.Property(x => x.Name).HasColumnName("Name").IsRequired();
        builder.Property(x => x.Created).HasColumnName("Created");
        builder.Property(x => x.Preset).HasColumnName("Preset");
    }
}

public class ExportHeaderConfiguration : IEntityTypeConfiguration<ExportHeader>
{
    public void Configure(EntityTypeBuilder<ExportHeader> builder)
    {
        builder.ToTable("ExportHeader", MlDbContext.Schema);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.CreateDate).HasColumnName("CreateDate");
        builder.Property(x => x.ExportStatusId).HasColumnName("ExportStatusId");
        builder.Property(x => x.ExternalUserId).HasColumnName("ExternalUserId");
        builder.Property(x => x.ArchiveName).HasColumnName("ArchiveName").IsRequired();
        builder.Property(x => x.IsNotificationSent).HasColumnName("IsNotificationSent");
        builder.Property(x => x.Result).HasColumnName("Result");
        builder.Property(x => x.Description).HasColumnName("Desc");
    }
}

public class ExportDetailConfiguration : IEntityTypeConfiguration<ExportDetail>
{
    public void Configure(EntityTypeBuilder<ExportDetail> builder)
    {
        builder.ToTable("ExportDetail", MlDbContext.Schema);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.ExportHeaderId).HasColumnName("ExportHeaderId");
        builder.Property(x => x.ProductVariantId).HasColumnName("ProductVariantId");
        builder.Property(x => x.MediaId).HasColumnName("MediaId");
        builder.Property(x => x.SourcePath).HasColumnName("SourcePath");
        builder.Property(x => x.DestinationPath).HasColumnName("DestinationPath");
    }
}

public class MlSettingsConfiguration : IEntityTypeConfiguration<MlSettings>
{
    public void Configure(EntityTypeBuilder<MlSettings> builder)
    {
        builder.ToTable("Settings", MlDbContext.Schema);

        // The table has no primary key, so EF needs a keyless mapping to query it.
        builder.HasNoKey();

        builder.Property(x => x.MediaLibraryLocalPath).HasColumnName("MediaLibraryLocalPath");
        builder.Property(x => x.MediaLibraryUncPath).HasColumnName("MediaLibraryUncPath");
        builder.Property(x => x.PhotoLabInboxVcatUncPath).HasColumnName("PhotoLabInboxVcatUncPath");
        builder.Property(x => x.PhotoLabInboxSalesUncPath).HasColumnName("PhotoLabInboxSalesUncPath");
        builder.Property(x => x.PhotoLabErrorsUncPath).HasColumnName("PhotoLabErrorsUncPath");
        builder.Property(x => x.VCatWidthInPixels).HasColumnName("VCatWidthInPixels");
        builder.Property(x => x.VCatQualityPercent).HasColumnName("VCatQualityPercent");
        builder.Property(x => x.MediaLibraryUrl).HasColumnName("MediaLibraryUrl");
        builder.Property(x => x.PhotoLabArchiveUncPath).HasColumnName("PhotoLabArchiveUncPath");
        builder.Property(x => x.VCatThumbnailWidthInPixels).HasColumnName("VCatThumbnailWidthInPixels");
        builder.Property(x => x.VCatThumbnailQualityPercent).HasColumnName("VCatThumbnailQualityPercent");
        builder.Property(x => x.PreliminaryInboxVcatUncPath).HasColumnName("PreliminaryInboxVcatUncPath");
        builder.Property(x => x.ExportLocalPath).HasColumnName("ExportLocalPath");
        builder.Property(x => x.ExportUncPath).HasColumnName("ExportUncPath");
        builder.Property(x => x.ExportServiceExeFullPath).HasColumnName("ExportServiceExeFullPath");
        builder.Property(x => x.ExportUrl).HasColumnName("ExportUrl");
        builder.Property(x => x.ImportServiceExeFullPath).HasColumnName("ImportServiceExeFullPath");
        builder.Property(x => x.ImportServiceStarted).HasColumnName("ImportServiceStarted");
        builder.Property(x => x.ExportExpiresHours).HasColumnName("ExportExpiresHours");
        builder.Property(x => x.SystemId).HasColumnName("SystemId");
        builder.Property(x => x.UserRoleId).HasColumnName("UserRoleId");
        builder.Property(x => x.ManagerRoleId).HasColumnName("ManagerRoleId");
        builder.Property(x => x.PhotoLabInboxNoValidationVcatUncPath).HasColumnName("PhotoLabInboxNoValidationVcatUncPath");
        builder.Property(x => x.PhotoLabInboxCadsUncPath).HasColumnName("PhotoLabInboxCadsUncPath");
        builder.Property(x => x.PhotoLabInboxCadstUncPath).HasColumnName("PhotoLabInboxCadstUncPath");
    }
}