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

    // ?? LoRA Helper ??????????????????????????????????????????

    public List<LoraSourceExport> LoraSources { get; init; } = [];
    public List<ImageGalleryExport> ImageGalleries { get; init; } = [];
    public bool ShowNsfw { get; init; }
    public bool GenerateVideoThumbnails { get; init; } = true;
    public bool ShowVideoPreview { get; init; }
    public bool UseForgeStylePrompts { get; init; } = true;
    public bool MergeLoraSources { get; init; }

    // ?? LoRA Sort ????????????????????????????????????????????

    public string? LoraSortSourcePath { get; init; }
    public string? LoraSortTargetPath { get; init; }
    public bool DeleteEmptySourceFolders { get; init; }

    // ?? Dataset Helper ???????????????????????????????????????

    public string? DatasetStoragePath { get; init; }
    public List<DatasetCategoryExport> DatasetCategories { get; init; } = [];
    public bool AutoBackupEnabled { get; init; }
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
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Minimum schema version that can still be imported.
    /// </summary>
    public const int MinSupportedVersion = 1;
}
