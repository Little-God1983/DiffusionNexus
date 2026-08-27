using System.Text.Json.Serialization;

namespace DiffusionNexus.Domain.Models;

/// <summary>
/// Root DTO for settings export/import. Carries a schema version
/// so that older exports can be imported into newer app versions.
/// Unknown JSON properties are silently ignored during deserialization,
/// ensuring forward and backward compatibility.
/// </summary>
public sealed record SettingsExportData
{
    /// <summary>
    /// Schema version of this export file.
    /// Increment when adding new fields so the importer can detect older formats.
    /// </summary>
    public int SchemaVersion { get; init; } = SettingsExportSchema.CurrentVersion;

    /// <summary>
    /// The application version that produced this export (informational).
    /// </summary>
    public string? AppVersion { get; init; }

    /// <summary>
    /// UTC timestamp when the export was created.
    /// </summary>
    public DateTimeOffset ExportedAt { get; init; } = DateTimeOffset.UtcNow;

    // ?? General ??????????????????????????????????????????????

    /// <summary>
    /// Encrypted Civitai API key. Machine-specific; may need re-entry after import.
    /// </summary>
    public string? EncryptedCivitaiApiKey { get; init; }

    /// <summary>
    /// Encrypted HuggingFace access token. Machine-specific; may need re-entry after import.
    /// </summary>
    public string? EncryptedHuggingfaceApiKey { get; init; }

    // ?? LoRA Helper ??????????????????????????????????????????

    public List<LoraSourceExport> LoraSources { get; init; } = [];
    public List<ImageGalleryExport> ImageGalleries { get; init; } = [];
    public List<BaseModelFolderExport> BaseModelFolders { get; init; } = [];
    public bool ShowNsfw { get; init; }
    public bool GenerateVideoThumbnails { get; init; } = true;
    public bool ShowVideoPreview { get; init; }
    public bool UseForgeStylePrompts { get; init; } = true;
    public bool MergeLoraSources { get; init; }
    public int LoraUpdateCheckStalenessDays { get; init; } = 3;

    // ?? Metadata Sync ????????????????????????????????????????

    /// <summary>Days before the bulk metadata sync re-checks a not-identified model. Default 30.</summary>
    public int SyncNotIdentifiedRetryDays { get; init; } = 30;

    /// <summary>Days before the bulk metadata sync retries a model whose last attempt errored. Default 1.</summary>
    public int SyncErrorRetryDays { get; init; } = 1;

    /// <summary>Parallel thumbnail downloads during a bulk sync. Default 4.</summary>
    public int SyncThumbnailConcurrency { get; init; } = 4;

    // Deliberately no LastLibrarySyncAt here: it is machine-local bookkeeping
    // (stamped by the sync flow itself, not a user-chosen setting), so it is
    // not part of the portable export/import contract.

    // ?? LoRA Sort ????????????????????????????????????????????

    public string? LoraSortSourcePath { get; init; }
    public string? LoraSortTargetPath { get; init; }
    public string? LoraSorterExcludedFoldersJson { get; init; }
    public bool DeleteEmptySourceFolders { get; init; }

    // ?? Dataset Helper ???????????????????????????????????????

    public string? DatasetStoragePath { get; init; }
    public List<DatasetCategoryExport> DatasetCategories { get; init; } = [];

    /// <summary>
    /// Whether automatic backup of the dataset-image folders is enabled. Nullable so
    /// import can distinguish "absent from the document" (fall back to
    /// <see cref="LegacyAutoBackupEnabled"/>, then <see langword="false"/>) from an
    /// explicit <see langword="false"/> (which must win outright). Always written as
    /// an explicit <see langword="true"/>/<see langword="false"/> on export.
    /// </summary>
    public bool? BackupDatasetImagesEnabled { get; init; }

    /// <summary>Whether automatic backup of the core user database is enabled. Defaults to true.</summary>
    public bool BackupDatabaseEnabled { get; init; } = true;

    /// <summary>
    /// Legacy (schema v1) field name for <see cref="BackupDatasetImagesEnabled"/>. Populated only when
    /// importing a v1 export so the user's original choice is preserved. Never written on export
    /// (null is omitted), so newer files carry only the new field.
    /// </summary>
    [JsonPropertyName("autoBackupEnabled")]
    public bool? LegacyAutoBackupEnabled { get; init; }

    public int AutoBackupIntervalDays { get; init; } = 1;
    public int AutoBackupIntervalHours { get; init; }
    public string? AutoBackupLocation { get; init; }
    public int MaxBackups { get; init; } = 10;

    // ?? ComfyUI ??????????????????????????????????????????????

    public string ComfyUiServerUrl { get; init; } = "http://127.0.0.1:8188/";
}

/// <summary>
/// Exported LoRA source folder.
/// </summary>
public sealed record LoraSourceExport
{
    public string FolderPath { get; init; } = string.Empty;
    public bool IsEnabled { get; init; } = true;
    public int Order { get; init; }
}

/// <summary>
/// Exported image gallery source folder.
/// </summary>
public sealed record ImageGalleryExport
{
    public string FolderPath { get; init; } = string.Empty;
    public bool IsEnabled { get; init; } = true;
    public int Order { get; init; }
}

/// <summary>
/// Exported Base Model Folder (model storage root). The installer-package link is
/// machine-specific and intentionally not exported.
/// </summary>
public sealed record BaseModelFolderExport
{
    public string FolderPath { get; init; } = string.Empty;
    public bool IsEnabled { get; init; } = true;
    public int Order { get; init; }
    public bool IsDefault { get; init; }
}

/// <summary>
/// Exported dataset category.
/// </summary>
public sealed record DatasetCategoryExport
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public bool IsDefault { get; init; }
    public int Order { get; init; }
}

/// <summary>
/// Tracks the current schema version for export files.
/// Bump <see cref="CurrentVersion"/> whenever new fields are added to
/// <see cref="SettingsExportData"/> so the importer can detect older formats.
/// </summary>
public static class SettingsExportSchema
{
    /// <summary>
    /// Current schema version. Bump when adding new fields.
    /// v2: split AutoBackupEnabled into BackupDatasetImagesEnabled + BackupDatabaseEnabled.
    /// v3: metadata-sync retry windows + thumbnail concurrency.
    /// </summary>
    /// <remarks>
    /// Bumping is cheap in both directions. The importer enforces only
    /// <see cref="MinSupportedVersion"/> — there is no upper bound — so v1 and v2 files still
    /// import into this build, and an older build reading a v3 file simply ignores the three
    /// unknown JSON properties. What the bump buys is the distinction a later migration would
    /// otherwise have no signal for: whether a field is absent because the file predates the
    /// feature or because the user chose nothing — exactly what
    /// <see cref="SettingsExportData.LegacyAutoBackupEnabled"/> needed for the v1→v2 split.
    /// </remarks>
    public const int CurrentVersion = 3;

    /// <summary>
    /// Minimum schema version that can still be imported.
    /// </summary>
    public const int MinSupportedVersion = 1;
}
