using DiffusionNexus.Domain.Entities;
using Microsoft.EntityFrameworkCore;

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
        // Set SQLite pragmas for better performance and to prevent indefinite hangs
        Database.SetCommandTimeout(TimeSpan.FromSeconds(30));
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
    public DbSet<ImageGallery> ImageGalleries => Set<ImageGallery>();

    /// <summary>Installed packages (ComfyUI, A1111, etc.).</summary>
    public DbSet<InstallerPackage> InstallerPackages => Set<InstallerPackage>();

    #endregion

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DiffusionNexusCoreDbContext).Assembly);
    }

    /// <summary>
    /// Overrides SaveChangesAsync to automatically populate audit fields
    /// (<see cref="BaseEntity.CreatedAt"/> and <see cref="BaseEntity.UpdatedAt"/>)
    /// on entities that inherit from <see cref="BaseEntity"/>.
    /// </summary>
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var entry in ChangeTracker.Entries<BaseEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity.CreatedAt == default)
                        entry.Entity.CreatedAt = now;
                    break;

                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;
                    break;
            }
        }

        return await base.SaveChangesAsync(cancellationToken);
    }

    #region Helpers

    /// <summary>
    /// Gets the default connection string for the core database.
    /// Checks for portable database (next to executable) first, then falls back to AppData.
    /// </summary>
    /// <param name="directory">Directory to store the database. Uses portable-first resolution if null.</param>
    public static string GetConnectionString(string? directory = null)
    {
        var dir = directory ?? GetDatabaseDirectory();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, DatabaseFileName);
        // Use default timeout and disable pooling to prevent connection locking issues
        return $"Data Source={path};Mode=ReadWriteCreate;Cache=Shared;Pooling=False;Default Timeout=30";
    }

    /// <summary>
    /// Gets the database directory using portable-first resolution.
    /// 1. First checks for database in executable folder (portable mode)
    /// 2. Falls back to %LOCALAPPDATA%/DiffusionNexus/Data/
    /// </summary>
    public static string GetDatabaseDirectory()
    {
        // Check for portable database next to executable first
        var exeDirectory = GetExecutableDirectory();
        if (exeDirectory is not null)
        {
            var portableDbPath = Path.Combine(exeDirectory, DatabaseFileName);
            if (File.Exists(portableDbPath))
            {
                return exeDirectory;
            }
        }

        // Fall back to AppData location
        return GetDefaultDatabaseDirectory();
    }

    /// <summary>
    /// Gets the directory where the executable is located.
    /// Returns null if it cannot be determined.
    /// </summary>
    private static string? GetExecutableDirectory()
    {
        try
        {
            // Get the directory of the main executable
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                return Path.GetDirectoryName(exePath);
            }

            // Fallback: use AppContext.BaseDirectory (works with single-file apps)
            var baseDir = AppContext.BaseDirectory;
            if (!string.IsNullOrEmpty(baseDir))
            {
                return baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }
        catch
        {
            // Ignore errors and fall back to default
        }

        return null;
    }

    /// <summary>
    /// Gets the default database directory in AppData.
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
