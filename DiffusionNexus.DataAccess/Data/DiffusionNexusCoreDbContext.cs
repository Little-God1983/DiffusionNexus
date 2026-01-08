using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DiffusionNexus.DataAccess.Data;

/// <summary>
/// DbContext for the DiffusionNexus core database using Domain entities.
/// Database: Diffusion_Nexus-core.db
/// </summary>
public class DiffusionNexusCoreDbContext : DbContext
{
    /// <summary>
    /// Default database filename.
    /// </summary>
    public const string DatabaseFileName = "Diffusion_Nexus-core.db";

    public DiffusionNexusCoreDbContext(DbContextOptions<DiffusionNexusCoreDbContext> options)
        : base(options)
    {
    }

    #region DbSets

    /// <summary>Models (aggregate root).</summary>
    public DbSet<Model> Models => Set<Model>();

    /// <summary>Model versions.</summary>
    public DbSet<ModelVersion> ModelVersions => Set<ModelVersion>();

    /// <summary>Downloadable files.</summary>
    public DbSet<ModelFile> ModelFiles => Set<ModelFile>();

    /// <summary>Preview images.</summary>
    public DbSet<ModelImage> ModelImages => Set<ModelImage>();

    /// <summary>Model creators.</summary>
    public DbSet<Creator> Creators => Set<Creator>();

    /// <summary>Tags.</summary>
    public DbSet<Tag> Tags => Set<Tag>();

    /// <summary>Model-Tag relationships.</summary>
    public DbSet<ModelTag> ModelTags => Set<ModelTag>();

    /// <summary>Trigger words.</summary>
    public DbSet<TriggerWord> TriggerWords => Set<TriggerWord>();

    /// <summary>Application settings (singleton).</summary>
    public DbSet<AppSettings> AppSettings => Set<AppSettings>();

    /// <summary>LoRA source folders.</summary>
    public DbSet<LoraSource> LoraSources => Set<LoraSource>();

    /// <summary>Dataset categories for organizing training datasets.</summary>
    public DbSet<DatasetCategory> DatasetCategories => Set<DatasetCategory>();

    /// <summary>Disclaimer acceptances.</summary>
    public DbSet<DisclaimerAcceptance> DisclaimerAcceptances => Set<DisclaimerAcceptance>();

    #endregion

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureModel(modelBuilder);
        ConfigureModelVersion(modelBuilder);
        ConfigureModelFile(modelBuilder);
        ConfigureModelImage(modelBuilder);
        ConfigureCreator(modelBuilder);
        ConfigureTag(modelBuilder);
        ConfigureModelTag(modelBuilder);
        ConfigureTriggerWord(modelBuilder);
        ConfigureAppSettings(modelBuilder);
        ConfigureLoraSource(modelBuilder);
        ConfigureDatasetCategory(modelBuilder);
        ConfigureDisclaimerAcceptance(modelBuilder);
    }

    #region Entity Configurations

    private static void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Model>(entity =>
        {
            entity.ToTable("Models");
            entity.HasKey(e => e.Id);

            // Indexes
            entity.HasIndex(e => e.CivitaiId).IsUnique().HasFilter("[CivitaiId] IS NOT NULL");
            entity.HasIndex(e => e.Name);
            entity.HasIndex(e => e.Type);
            entity.HasIndex(e => e.CreatedAt);

            // Properties
            entity.Property(e => e.Name).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Description).HasColumnType("TEXT");
            entity.Property(e => e.Type).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.Mode).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.Source).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.AllowCommercialUse).HasConversion<string>().HasMaxLength(20);

            // Relationships
            entity.HasOne(e => e.Creator)
                .WithMany(c => c.Models)
                .HasForeignKey(e => e.CreatorId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasMany(e => e.Versions)
                .WithOne(v => v.Model)
                .HasForeignKey(v => v.ModelId)
                .OnDelete(DeleteBehavior.Cascade);

            // Ignore computed properties
            entity.Ignore(e => e.LatestVersion);
            entity.Ignore(e => e.TotalDownloads);
            entity.Ignore(e => e.PrimaryImage);
        });
    }

    private static void ConfigureModelVersion(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ModelVersion>(entity =>
        {
            entity.ToTable("ModelVersions");
            entity.HasKey(e => e.Id);

            // Indexes
            entity.HasIndex(e => e.CivitaiId).IsUnique().HasFilter("[CivitaiId] IS NOT NULL");
            entity.HasIndex(e => e.ModelId);
            entity.HasIndex(e => e.BaseModel);
            entity.HasIndex(e => e.CreatedAt);

            // Properties
            entity.Property(e => e.Name).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Description).HasColumnType("TEXT");
            entity.Property(e => e.BaseModel).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.BaseModelRaw).HasMaxLength(100);
            entity.Property(e => e.DownloadUrl).HasMaxLength(2000);

            // Relationships
            entity.HasMany(e => e.Files)
                .WithOne(f => f.ModelVersion)
                .HasForeignKey(f => f.ModelVersionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Images)
                .WithOne(i => i.ModelVersion)
                .HasForeignKey(i => i.ModelVersionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.TriggerWords)
                .WithOne(t => t.ModelVersion)
                .HasForeignKey(t => t.ModelVersionId)
                .OnDelete(DeleteBehavior.Cascade);

            // Ignore computed properties
            entity.Ignore(e => e.PrimaryFile);
            entity.Ignore(e => e.PrimaryImage);
            entity.Ignore(e => e.TriggerWordsText);
        });
    }

    private static void ConfigureModelFile(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ModelFile>(entity =>
        {
            entity.ToTable("ModelFiles");
            entity.HasKey(e => e.Id);

            // Indexes
            entity.HasIndex(e => e.CivitaiId);
            entity.HasIndex(e => e.ModelVersionId);
            entity.HasIndex(e => e.HashSHA256);
            entity.HasIndex(e => e.LocalPath);
            entity.HasIndex(e => e.FileSizeBytes);

            // Properties
            entity.Property(e => e.FileName).IsRequired().HasMaxLength(500);
            entity.Property(e => e.FileType).HasMaxLength(50);
            entity.Property(e => e.Format).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.Precision).HasConversion<string>().HasMaxLength(10);
            entity.Property(e => e.SizeType).HasConversion<string>().HasMaxLength(10);
            entity.Property(e => e.PickleScanResult).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.VirusScanResult).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.DownloadUrl).HasMaxLength(2000);
            entity.Property(e => e.LocalPath).HasMaxLength(1000);
            entity.Property(e => e.HashAutoV1).HasMaxLength(20);
            entity.Property(e => e.HashAutoV2).HasMaxLength(20);
            entity.Property(e => e.HashSHA256).HasMaxLength(64);
            entity.Property(e => e.HashCRC32).HasMaxLength(10);
            entity.Property(e => e.HashBLAKE3).HasMaxLength(64);
            entity.Property(e => e.PickleScanMessage).HasMaxLength(1000);

            // Ignore computed properties
            entity.Ignore(e => e.SizeMB);
            entity.Ignore(e => e.SizeGB);
            entity.Ignore(e => e.SizeDisplay);
            entity.Ignore(e => e.IsSecure);
        });
    }

    private static void ConfigureModelImage(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ModelImage>(entity =>
        {
            entity.ToTable("ModelImages");
            entity.HasKey(e => e.Id);

            // Indexes
            entity.HasIndex(e => e.CivitaiId);
            entity.HasIndex(e => e.ModelVersionId);
            entity.HasIndex(e => new { e.ModelVersionId, e.SortOrder });

            // Properties
            entity.Property(e => e.Url).IsRequired().HasMaxLength(2000);
            entity.Property(e => e.NsfwLevel).HasConversion<string>().HasMaxLength(10);
            entity.Property(e => e.BlurHash).HasMaxLength(100);
            entity.Property(e => e.Username).HasMaxLength(200);
            entity.Property(e => e.LocalCachePath).HasMaxLength(500);

            // Thumbnail BLOB
            entity.Property(e => e.ThumbnailData).HasColumnType("BLOB");
            entity.Property(e => e.ThumbnailMimeType).HasMaxLength(50);

            // Generation metadata
            entity.Property(e => e.Prompt).HasColumnType("TEXT");
            entity.Property(e => e.NegativePrompt).HasColumnType("TEXT");
            entity.Property(e => e.Sampler).HasMaxLength(100);
            entity.Property(e => e.GenerationModel).HasMaxLength(200);

            // Ignore computed properties
            entity.Ignore(e => e.AspectRatio);
            entity.Ignore(e => e.IsPortrait);
            entity.Ignore(e => e.IsLandscape);
            entity.Ignore(e => e.HasThumbnail);
            entity.Ignore(e => e.HasLocalCache);
            entity.Ignore(e => e.IsPrimary);
            entity.Ignore(e => e.ThumbnailSizeKB);
        });
    }

    private static void ConfigureCreator(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Creator>(entity =>
        {
            entity.ToTable("Creators");
            entity.HasKey(e => e.Id);

            // Indexes
            entity.HasIndex(e => e.Username).IsUnique();

            // Properties
            entity.Property(e => e.Username).IsRequired().HasMaxLength(200);
            entity.Property(e => e.AvatarUrl).HasMaxLength(2000);

            // Ignore computed properties
            entity.Ignore(e => e.ModelCount);
        });
    }

    private static void ConfigureTag(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tag>(entity =>
        {
            entity.ToTable("Tags");
            entity.HasKey(e => e.Id);

            // Indexes
            entity.HasIndex(e => e.NormalizedName).IsUnique();

            // Properties
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.NormalizedName).IsRequired().HasMaxLength(200);
        });
    }

    private static void ConfigureModelTag(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ModelTag>(entity =>
        {
            entity.ToTable("ModelTags");

            // Composite primary key
            entity.HasKey(e => new { e.ModelId, e.TagId });

            // Relationships
            entity.HasOne(e => e.Model)
                .WithMany(m => m.Tags)
                .HasForeignKey(e => e.ModelId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Tag)
                .WithMany(t => t.Models)
                .HasForeignKey(e => e.TagId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureTriggerWord(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TriggerWord>(entity =>
        {
            entity.ToTable("TriggerWords");
            entity.HasKey(e => e.Id);

            // Indexes
            entity.HasIndex(e => e.ModelVersionId);

            // Properties
            entity.Property(e => e.Word).IsRequired().HasMaxLength(500);
        });
    }

    private static void ConfigureAppSettings(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppSettings>(entity =>
        {
            entity.ToTable("AppSettings");
            entity.HasKey(e => e.Id);

            // Properties
            entity.Property(e => e.EncryptedCivitaiApiKey).HasMaxLength(2000);
            entity.Property(e => e.LoraSortSourcePath).HasMaxLength(1000);
            entity.Property(e => e.LoraSortTargetPath).HasMaxLength(1000);
            entity.Property(e => e.DatasetStoragePath).HasMaxLength(1000);
            entity.Property(e => e.AutoBackupLocation).HasMaxLength(1000);

            // Relationships
            entity.HasMany(e => e.LoraSources)
                .WithOne(s => s.AppSettings)
                .HasForeignKey(s => s.AppSettingsId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.DatasetCategories)
                .WithOne(c => c.AppSettings)
                .HasForeignKey(c => c.AppSettingsId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureLoraSource(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LoraSource>(entity =>
        {
            entity.ToTable("LoraSources");
            entity.HasKey(e => e.Id);

            // Indexes
            entity.HasIndex(e => e.AppSettingsId);
            entity.HasIndex(e => e.FolderPath);

            // Properties
            entity.Property(e => e.FolderPath).IsRequired().HasMaxLength(1000);
        });
    }

    private static void ConfigureDatasetCategory(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DatasetCategory>(entity =>
        {
            entity.ToTable("DatasetCategories");
            entity.HasKey(e => e.Id);

            // Indexes
            entity.HasIndex(e => e.AppSettingsId);
            entity.HasIndex(e => e.Name);

            // Properties
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(500);

            // NOTE: Default categories are seeded at runtime via IAppSettingsService
            // to ensure AppSettings row exists first
        });
    }

    private static void ConfigureDisclaimerAcceptance(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DisclaimerAcceptance>(entity =>
        {
            entity.ToTable("DisclaimerAcceptances");
            entity.HasKey(e => e.Id);

            // Indexes
            entity.HasIndex(e => e.WindowsUsername);

            // Properties
            entity.Property(e => e.WindowsUsername).IsRequired().HasMaxLength(256);
        });
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Gets the default connection string for the core database.
    /// </summary>
    /// <param name="directory">Directory to store the database. Uses app data if null.</param>
    public static string GetConnectionString(string? directory = null)
    {
        var dir = directory ?? GetDefaultDatabaseDirectory();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, DatabaseFileName);
        return $"Data Source={path}";
    }

    /// <summary>
    /// Gets the default database directory.
    /// </summary>
    public static string GetDefaultDatabaseDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "DiffusionNexus", "Data");
    }

    /// <summary>
    /// Creates DbContextOptions for the core database.
    /// </summary>
    public static DbContextOptions<DiffusionNexusCoreDbContext> CreateOptions(string? directory = null)
    {
        return new DbContextOptionsBuilder<DiffusionNexusCoreDbContext>()
            .UseSqlite(GetConnectionString(directory))
            .Options;
    }

    #endregion
}
