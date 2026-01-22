namespace DiffusionNexus.Domain.Entities;

/// <summary>
/// Application settings stored in the database.
/// Singleton entity (only one row with Id = 1).
/// </summary>
public class AppSettings
{
    /// <summary>
    /// Settings ID. Always 1 (singleton pattern).
    /// </summary>
    public int Id { get; set; } = 1;

    #region API Keys

    /// <summary>
    /// Encrypted Civitai API key.
    /// Use ISecureStorage to encrypt/decrypt.
    /// </summary>
    public string? EncryptedCivitaiApiKey { get; set; }

    #endregion

    #region LoRA Helper Settings

    /// <summary>
    /// Collection of LoRA source folders.
    /// </summary>
    public ICollection<LoraSource> LoraSources { get; set; } = new List<LoraSource>();

    /// <summary>
    /// Collection of Image Gallery source folders.
    /// </summary>
    public ICollection<ImageGallery> ImageGalleries { get; set; } = new List<ImageGallery>();

    /// <summary>
    /// Whether to show NSFW content by default.
    /// </summary>
    public bool ShowNsfw { get; set; }

    /// <summary>
    /// Whether to automatically generate thumbnails from video files.
    /// </summary>
    public bool GenerateVideoThumbnails { get; set; } = true;

    /// <summary>
    /// Whether to show video preview (experimental, slow).
    /// </summary>
    public bool ShowVideoPreview { get; set; }

    /// <summary>
    /// Whether to use A1111/Forge style prompts.
    /// </summary>
    public bool UseForgeStylePrompts { get; set; } = true;

    /// <summary>
    /// Whether to merge LoRA sources by base model.
    /// </summary>
    public bool MergeLoraSources { get; set; }

    #endregion

    #region LoRA Sort Settings

    /// <summary>
    /// Default source folder for LoRA Sort.
    /// </summary>
    public string? LoraSortSourcePath { get; set; }

    /// <summary>
    /// Default target folder for LoRA Sort.
    /// </summary>
    public string? LoraSortTargetPath { get; set; }

    /// <summary>
    /// Whether to delete empty source folders after sorting.
    /// </summary>
    public bool DeleteEmptySourceFolders { get; set; }

    #endregion

    #region LoRA Dataset Helper Settings

    /// <summary>
    /// Default storage path for LoRA training datasets.
    /// Each dataset will be created as a subfolder within this path.
    /// </summary>
    public string? DatasetStoragePath { get; set; }

    /// <summary>
    /// Collection of dataset categories (Character, Style, Concept, custom...).
    /// </summary>
    public ICollection<DatasetCategory> DatasetCategories { get; set; } = new List<DatasetCategory>();

    /// <summary>
    /// Whether automatic backup is enabled for datasets.
    /// </summary>
    public bool AutoBackupEnabled { get; set; }

    /// <summary>
    /// Days component of the automatic backup interval (1-30).
    /// </summary>
    public int AutoBackupIntervalDays { get; set; } = 1;

    /// <summary>
    /// Hours component of the automatic backup interval (0-23).
    /// </summary>
    public int AutoBackupIntervalHours { get; set; }

    /// <summary>
    /// Folder path where automatic backups are stored.
    /// Cannot be the same as or a subfolder of DatasetStoragePath.
    /// </summary>
    public string? AutoBackupLocation { get; set; }

    /// <summary>
    /// When the last automatic backup was performed.
    /// </summary>
    public DateTimeOffset? LastBackupAt { get; set; }

    /// <summary>
    /// Maximum number of backups to keep. Oldest backups are deleted when this limit is exceeded.
    /// Default is 10.
    /// </summary>
    public int MaxBackups { get; set; } = 10;

    #endregion

    #region Timestamps

    /// <summary>
    /// When settings were last modified.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    #endregion
}
