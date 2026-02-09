using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Exports and imports application settings as versioned JSON files.
/// <para>
/// Forward compatibility: unknown JSON properties in newer exports are
/// silently skipped by <see cref="JsonUnmappedMemberHandling.Skip"/>.
/// </para>
/// <para>
/// Backward compatibility: new properties added in later schema versions
/// fall back to their declared default values when missing from older exports.
/// </para>
/// </summary>
public sealed class SettingsExportService : ISettingsExportService
{
    private readonly IAppSettingsService _settingsService;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };

    public SettingsExportService(IAppSettingsService settingsService)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        _settingsService = settingsService;
    }

    /// <inheritdoc />
    public async Task ExportAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var settings = await _settingsService.GetSettingsAsync(cancellationToken).ConfigureAwait(false);

        var appVersion = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        var export = new SettingsExportData
        {
            SchemaVersion = SettingsExportSchema.CurrentVersion,
            AppVersion = appVersion,
            ExportedAt = DateTimeOffset.UtcNow,

            EncryptedCivitaiApiKey = settings.EncryptedCivitaiApiKey,

            ShowNsfw = settings.ShowNsfw,
            GenerateVideoThumbnails = settings.GenerateVideoThumbnails,
            ShowVideoPreview = settings.ShowVideoPreview,
            UseForgeStylePrompts = settings.UseForgeStylePrompts,
            MergeLoraSources = settings.MergeLoraSources,

            LoraSortSourcePath = settings.LoraSortSourcePath,
            LoraSortTargetPath = settings.LoraSortTargetPath,
            DeleteEmptySourceFolders = settings.DeleteEmptySourceFolders,

            DatasetStoragePath = settings.DatasetStoragePath,
            AutoBackupEnabled = settings.AutoBackupEnabled,
            AutoBackupIntervalDays = settings.AutoBackupIntervalDays,
            AutoBackupIntervalHours = settings.AutoBackupIntervalHours,
            AutoBackupLocation = settings.AutoBackupLocation,
            MaxBackups = settings.MaxBackups,

            ComfyUiServerUrl = settings.ComfyUiServerUrl
        };

        var order = 0;
        foreach (var s in settings.LoraSources.OrderBy(x => x.Order))
        {
            export.LoraSources.Add(new LoraSourceExport
            {
                FolderPath = s.FolderPath,
                IsEnabled = s.IsEnabled,
                Order = order++
            });
        }

        order = 0;
        foreach (var g in settings.ImageGalleries.OrderBy(x => x.Order))
        {
            export.ImageGalleries.Add(new ImageGalleryExport
            {
                FolderPath = g.FolderPath,
                IsEnabled = g.IsEnabled,
                Order = order++
            });
        }

        order = 0;
        foreach (var c in settings.DatasetCategories.OrderBy(x => x.Order))
        {
            export.DatasetCategories.Add(new DatasetCategoryExport
            {
                Name = c.Name,
                Description = c.Description,
                IsDefault = c.IsDefault,
                Order = order++
            });
        }

        var json = JsonSerializer.Serialize(export, JsonOptions);
        await File.WriteAllTextAsync(filePath, json, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SettingsExportData> ReadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);

        var export = JsonSerializer.Deserialize<SettingsExportData>(json, JsonOptions)
            ?? throw new InvalidOperationException("The settings file is empty or contains invalid JSON.");

        if (export.SchemaVersion < SettingsExportSchema.MinSupportedVersion)
        {
            throw new InvalidOperationException(
                $"This settings file uses schema version {export.SchemaVersion}, " +
                $"but the minimum supported version is {SettingsExportSchema.MinSupportedVersion}.");
        }

        return export;
    }

    /// <inheritdoc />
    public async Task ImportAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var export = await ReadAsync(filePath, cancellationToken).ConfigureAwait(false);

        var settings = new AppSettings
        {
            Id = 1,
            EncryptedCivitaiApiKey = export.EncryptedCivitaiApiKey,

            ShowNsfw = export.ShowNsfw,
            GenerateVideoThumbnails = export.GenerateVideoThumbnails,
            ShowVideoPreview = export.ShowVideoPreview,
            UseForgeStylePrompts = export.UseForgeStylePrompts,
            MergeLoraSources = export.MergeLoraSources,

            LoraSortSourcePath = export.LoraSortSourcePath,
            LoraSortTargetPath = export.LoraSortTargetPath,
            DeleteEmptySourceFolders = export.DeleteEmptySourceFolders,

            DatasetStoragePath = export.DatasetStoragePath,
            AutoBackupEnabled = export.AutoBackupEnabled,
            AutoBackupIntervalDays = export.AutoBackupIntervalDays,
            AutoBackupIntervalHours = export.AutoBackupIntervalHours,
            AutoBackupLocation = export.AutoBackupLocation,
            MaxBackups = export.MaxBackups,

            ComfyUiServerUrl = export.ComfyUiServerUrl
        };

        foreach (var s in export.LoraSources.OrderBy(x => x.Order))
        {
            settings.LoraSources.Add(new LoraSource
            {
                AppSettingsId = 1,
                FolderPath = s.FolderPath,
                IsEnabled = s.IsEnabled,
                Order = s.Order
            });
        }

        foreach (var g in export.ImageGalleries.OrderBy(x => x.Order))
        {
            settings.ImageGalleries.Add(new ImageGallery
            {
                AppSettingsId = 1,
                FolderPath = g.FolderPath,
                IsEnabled = g.IsEnabled,
                Order = g.Order
            });
        }

        foreach (var c in export.DatasetCategories.OrderBy(x => x.Order))
        {
            settings.DatasetCategories.Add(new DatasetCategory
            {
                AppSettingsId = 1,
                Name = c.Name,
                Description = c.Description,
                IsDefault = c.IsDefault,
                Order = c.Order
            });
        }

        await _settingsService.SaveSettingsAsync(settings, cancellationToken).ConfigureAwait(false);
    }
}
