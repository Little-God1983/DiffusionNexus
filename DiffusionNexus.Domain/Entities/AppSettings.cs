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

    /// <summary>
    /// Encrypted HuggingFace access token. Required for downloading gated /
    /// private HuggingFace repositories (sent as an <c>Authorization: Bearer</c>
    /// header). Use ISecureStorage to encrypt/decrypt.
    /// </summary>
    public string? EncryptedHuggingfaceApiKey { get; set; }

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

    /// <summary>
    /// Maximum age (in days) of a model's <c>LastCheckedForUpdatesUtc</c> before
    /// the LoRA Viewer will silently re-check Civitai for new versions of the
    /// currently visible tiles. Defaults to 3 days; <c>0</c> disables the
    /// automatic update check entirely.
    /// </summary>
    public int LoraUpdateCheckStalenessDays { get; set; } = 3;

    #endregion

    #region LoRA Sort Settings

    /// <summary>
    /// Default source folder for LoRA Sort.
    /// </summary>
    public string? LoraSortSourcePath { get; set; }

    /// <summary>
    /// User-favorited LoRA source folder. When set, this folder is pre-selected
    /// in download destination pickers (Installed-tab "Download LoRA" dialog and
    /// the Civitai browser's queue destination panel). Null = no favorite, fall
    /// back to the first enabled source.
    /// </summary>
    public string? FavoriteLoraSourcePath { get; set; }

    /// <summary>
    /// Reporter e-mail remembered by the in-app feedback dialog (optional; pre-fills the
    /// dialog's e-mail field). Null when the user never entered one.
    /// </summary>
    public string? FeedbackReporterEmail { get; set; }

    /// <summary>
    /// The Batch Metadata Distiller's saved delete/replace rule sets, serialized as JSON
    /// (a list of sets: name, kind, enabled, delete words / replace pairs). Null when the
    /// user has never created any. Owned and (de)serialized by the distiller ViewModel.
    /// </summary>
    public string? DistillerRuleSetsJson { get; set; }

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

    #endregion

    #region Backup Settings

    /// <summary>
    /// Whether automatic backup of the dataset-image folders (zipped) is enabled.
    /// (Formerly <c>AutoBackupEnabled</c> — the DB column keeps that history via a rename migration.)
    /// </summary>
    public bool BackupDatasetImagesEnabled { get; set; }

    /// <summary>
    /// Whether automatic backup of the core user database (<c>Diffusion_Nexus-core.db</c>) is enabled.
    /// Defaults to <c>true</c> so a fresh install protects the user database by default.
    /// </summary>
    public bool BackupDatabaseEnabled { get; set; } = true;

    /// <summary>
    /// Days component of the automatic backup interval (1-30). Shared by both backup types.
    /// </summary>
    public int AutoBackupIntervalDays { get; set; } = 1;

    /// <summary>
    /// Hours component of the automatic backup interval (0-23). Shared by both backup types.
    /// </summary>
    public int AutoBackupIntervalHours { get; set; }

    /// <summary>
    /// Folder path where automatic backups (dataset zips and database copies) are stored.
    /// Cannot be the same as or a subfolder of DatasetStoragePath.
    /// </summary>
    public string? AutoBackupLocation { get; set; }

    /// <summary>
    /// When the last automatic backup was performed (either type).
    /// </summary>
    public DateTimeOffset? LastBackupAt { get; set; }

    /// <summary>
    /// Maximum number of backups to keep <b>per type</b>. Oldest are deleted when this limit is
    /// exceeded (dataset zips and database copies are pruned independently). Default is 10.
    /// </summary>
    public int MaxBackups { get; set; } = 10;

    #endregion

    #region ComfyUI Settings

    /// <summary>
    /// The ComfyUI server URL.
    /// </summary>
    public string ComfyUiServerUrl { get; set; } = "http://127.0.0.1:8188/";

    #endregion

    #region Timestamps

    /// <summary>
    /// When settings were last modified.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    #endregion
}
