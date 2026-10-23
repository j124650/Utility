using Microsoft.EntityFrameworkCore;
using Utility.Server.ML.Entities;

namespace Utility.Server.ML.Data;

/// <summary>
/// Strictly read-only context over the existing <c>ml.</c> schema.
/// <para>
/// Guarantees:
/// <list type="bullet">
/// <item><description><c>SaveChanges</c> and <c>SaveChangesAsync</c> throw.</description></item>
/// <item><description>No <c>Migrations</c> assembly — nothing here is ever created or altered.</description></item>
/// <item><description>Queries are no-tracking by default.</description></item>
/// </list>
/// </para>
/// </summary>
public class MlDbContext(DbContextOptions<MlDbContext> options) : DbContext(options)
{
    public const string Schema = "ml";

    public DbSet<Media> Media => Set<Media>();

    public DbSet<MediaType> MediaTypes => Set<MediaType>();

    public DbSet<MediaCategory> MediaCategories => Set<MediaCategory>();

    public DbSet<MediaSubCategory> MediaSubCategories => Set<MediaSubCategory>();

    public DbSet<MediaSubType> MediaSubTypes => Set<MediaSubType>();

    public DbSet<MediaTempCatalogPaths> MediaTempCatalogPaths => Set<MediaTempCatalogPaths>();

    public DbSet<MediaTempFilesNames> MediaTempFilesNames => Set<MediaTempFilesNames>();

    public DbSet<ExportStatus> ExportStatuses => Set<ExportStatus>();

    public DbSet<ExportTemplate> ExportTemplates => Set<ExportTemplate>();

    public DbSet<ExportHeader> ExportHeaders => Set<ExportHeader>();

    public DbSet<ExportDetail> ExportDetails => Set<ExportDetail>();

    public DbSet<MlSettings> Settings => Set<MlSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // These configurations live in Utility.Server.Data (the writable context's folder),
        // so they are applied explicitly rather than via assembly scanning.
        modelBuilder.ApplyConfiguration(new MlModelConfiguration());
        modelBuilder.ApplyConfiguration(new MediaTypeConfiguration());
        modelBuilder.ApplyConfiguration(new MediaCategoryConfiguration());
        modelBuilder.ApplyConfiguration(new MediaSubCategoryConfiguration());
        modelBuilder.ApplyConfiguration(new MediaSubTypeConfiguration());
        modelBuilder.ApplyConfiguration(new MediaTempCatalogPathsConfiguration());
        modelBuilder.ApplyConfiguration(new MediaTempFilesNamesConfiguration());
        modelBuilder.ApplyConfiguration(new ExportStatusConfiguration());
        modelBuilder.ApplyConfiguration(new ExportTemplateConfiguration());
        modelBuilder.ApplyConfiguration(new ExportHeaderConfiguration());
        modelBuilder.ApplyConfiguration(new ExportDetailConfiguration());
        modelBuilder.ApplyConfiguration(new MlSettingsConfiguration());
    }

    public override int SaveChanges() =>
        throw new NotSupportedException(
            "MlDbContext is read-only. The ml. schema is owned by another system and must not be modified.");

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "MlDbContext is read-only. The ml. schema is owned by another system and must not be modified.");

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "MlDbContext is read-only. The ml. schema is owned by another system and must not be modified.");
}