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

    #endregion

    #region Timestamps

    /// <summary>
    /// When settings were last modified.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    #endregion
}
