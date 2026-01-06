using DiffusionNexus.DataAccess.Entities;
using Microsoft.EntityFrameworkCore;

namespace DiffusionNexus.DataAccess.Data;

/// <summary>
/// Legacy DbContext for the original DiffusionNexus database.
/// Database: diffusion_nexus.db
/// </summary>
/// <remarks>
/// This context is used by DiffusionNexus.UI (V1) and related services.
/// For new development, use <see cref="DiffusionNexusCoreDbContext"/> instead.
/// </remarks>
[Obsolete("Use DiffusionNexusCoreDbContext for new development. This context is maintained for legacy UI compatibility.")]
public class DiffusionNexusDbContext : DbContext
{
    public DiffusionNexusDbContext(DbContextOptions<DiffusionNexusDbContext> options)
        : base(options)
    {
    }

    public DbSet<Model> Models { get; set; }
    public DbSet<ModelVersion> ModelVersions { get; set; }
    public DbSet<ModelFile> ModelFiles { get; set; }
    public DbSet<ModelImage> ModelImages { get; set; }
    public DbSet<ModelTag> ModelTags { get; set; }
    public DbSet<TrainedWord> TrainedWords { get; set; }
    public DbSet<AppSetting> AppSettings { get; set; }
    public DbSet<UserPreference> UserPreferences { get; set; }
    public DbSet<CustomTagMapping> CustomTagMappings { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Model>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.CivitaiModelId).IsUnique();
            entity.Property(e => e.Name).IsRequired();
            entity.Property(e => e.Type).IsRequired();
        });

        modelBuilder.Entity<ModelVersion>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.CivitaiVersionId).IsUnique();
            entity.Property(e => e.Name).IsRequired();
            entity.Property(e => e.BaseModel).IsRequired();

            entity.HasOne(e => e.Model)
                .WithMany(m => m.Versions)
                .HasForeignKey(e => e.ModelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ModelFile>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SHA256Hash);
            entity.HasIndex(e => e.LocalFilePath);
            entity.Property(e => e.Name).IsRequired();

            entity.HasOne(e => e.ModelVersion)
                .WithMany(v => v.Files)
                .HasForeignKey(e => e.ModelVersionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ModelImage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.LocalFilePath);
            entity.Property(e => e.Url).IsRequired();

            entity.HasOne(e => e.ModelVersion)
                .WithMany(v => v.Images)
                .HasForeignKey(e => e.ModelVersionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ModelTag>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ModelId, e.Tag }).IsUnique();
            entity.Property(e => e.Tag).IsRequired();

            entity.HasOne(e => e.Model)
                .WithMany(m => m.Tags)
                .HasForeignKey(e => e.ModelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TrainedWord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Word).IsRequired();

            entity.HasOne(e => e.ModelVersion)
                .WithMany(v => v.TrainedWords)
                .HasForeignKey(e => e.ModelVersionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AppSetting>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Key).IsUnique();
            entity.Property(e => e.Key).IsRequired();
        });

        modelBuilder.Entity<UserPreference>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.UserId, e.PreferenceKey }).IsUnique();
            entity.Property(e => e.UserId).IsRequired();
            entity.Property(e => e.PreferenceKey).IsRequired();
        });

        modelBuilder.Entity<CustomTagMapping>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Folder).IsRequired();
        });
    }
}
