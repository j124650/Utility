using Microsoft.EntityFrameworkCore;
using Utility.Server.DropBoxSync.Entities;

namespace Utility.Server.DropBoxSync;

/// <summary>
/// The one WRITABLE context in the solution. It maps only <c>ml.DropBoxNode</c>, the
/// table this app owns.
/// <para>
/// It is deliberately separate from <c>MlDbContext</c>, which maps the <c>ml.</c> tables
/// another system owns and throws on every SaveChanges. Nothing that can write may
/// reference <c>MlDbContext</c>, and nothing that reads someone else's tables may
/// reference this one.
/// </para>
/// <para>
/// No migrations assembly exists. The schema is owned by
/// <c>Sql/CreateDropBoxNode.sql</c> and applied by hand, so this context must never
/// call <c>Database.Migrate()</c> or <c>EnsureCreated()</c> — both would try to emit DDL.
/// </para>
/// </summary>
public class DropBoxDbContext(DbContextOptions<DropBoxDbContext> options) : DbContext(options)
{
    public const string Schema = "ml";

    public DbSet<DropBoxNode> Nodes => Set<DropBoxNode>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        var node = modelBuilder.Entity<DropBoxNode>();
        node.ToTable("DropBoxNode", Schema);

        node.HasKey(x => x.Id);
        node.Property(x => x.Id).ValueGeneratedOnAdd();

        // PathHash is a PERSISTED computed column maintained by the database.
        // ValueGeneratedOnAddOrUpdate tells EF to never insert or update it.
        node.Property(x => x.PathHash)
            .ValueGeneratedOnAddOrUpdate()
            .HasColumnName("PathHash");

        // The natural key an upsert matches on.
        node.HasIndex(x => new { x.NamespaceId, x.PathHash })
            .IsUnique()
            .HasDatabaseName("UQ_DropBoxNode_Path");

        node.HasIndex(x => x.ParentId).HasDatabaseName("IX_DropBoxNode_Parent");

        // NO ACTION, not CASCADE: SQL Server rejects a cascade on a self-referencing
        // FK (Msg 1785). Deleting a subtree in ONE statement is still allowed.
        node.HasOne<DropBoxNode>()
            .WithMany()
            .HasForeignKey(x => x.ParentId)
            .OnDelete(DeleteBehavior.NoAction);

        node.Property(x => x.Id).HasColumnName("Id");
        node.Property(x => x.NamespaceId).HasColumnName("NamespaceId");
        node.Property(x => x.PathDisplay).HasColumnName("PathDisplay");
        node.Property(x => x.PathLower).HasColumnName("PathLower");
        node.Property(x => x.Name).HasColumnName("Name");
        node.Property(x => x.IsFolder).HasColumnName("IsFolder");
        node.Property(x => x.ParentId).HasColumnName("ParentId");
        node.Property(x => x.DropboxId).HasColumnName("DropboxId");
        node.Property(x => x.SizeBytes).HasColumnName("SizeBytes");
        node.Property(x => x.Rev).HasColumnName("Rev");
        node.Property(x => x.ContentHash).HasColumnName("ContentHash");
        node.Property(x => x.WidthPixels).HasColumnName("WidthPixels");
        node.Property(x => x.HeightPixels).HasColumnName("HeightPixels");
        node.Property(x => x.MediaInfoTag).HasColumnName("MediaInfoTag");
        node.Property(x => x.ServerModified).HasColumnName("ServerModified");
        node.Property(x => x.ClientModified).HasColumnName("ClientModified");
        node.Property(x => x.IsDownloadable).HasColumnName("IsDownloadable");
        node.Property(x => x.SharedFolderId).HasColumnName("SharedFolderId");
        node.Property(x => x.SyncStatus).HasColumnName("SyncStatus");
        node.Property(x => x.SyncStatusReason).HasColumnName("SyncStatusReason");
        node.Property(x => x.SyncRunId).HasColumnName("SyncRunId");
        node.Property(x => x.SyncedUtc).HasColumnName("SyncedUtc");
    }
}