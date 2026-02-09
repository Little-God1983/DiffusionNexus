using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Service for managing application settings stored in the database.
/// </summary>
public sealed class AppSettingsService : IAppSettingsService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISecureStorage _secureStorage;

    public AppSettingsService(IUnitOfWork unitOfWork, ISecureStorage secureStorage)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(secureStorage);
        _unitOfWork = unitOfWork;
        _secureStorage = secureStorage;
    }

    /// <inheritdoc />
    public async Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _unitOfWork.AppSettings
            .GetSettingsWithIncludesAsync(cancellationToken)
            .ConfigureAwait(false);

        // Settings may be newly created and need saving
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (!settings.DatasetCategories.Any())
        {
            await SeedDefaultCategoriesAsync(cancellationToken).ConfigureAwait(false);

            // Reload to get the categories
            settings = await _unitOfWork.AppSettings
                .GetSettingsWithIncludesAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await RemoveDuplicateCategoriesAsync(settings, cancellationToken).ConfigureAwait(false);
        }

        return settings;
    }

    /// <summary>
    /// Removes duplicate categories keeping only the first one of each name.
    /// </summary>
    private async Task RemoveDuplicateCategoriesAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var categoriesByName = settings.DatasetCategories
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();

        if (categoriesByName.Count == 0)
            return;

        foreach (var group in categoriesByName)
        {
            var duplicates = group.Skip(1).ToList();
            foreach (var duplicate in duplicates)
            {
                _unitOfWork.AppSettings.RemoveDatasetCategory(duplicate);
                settings.DatasetCategories.Remove(duplicate);
            }
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SeedDefaultCategoriesAsync(CancellationToken cancellationToken = default)
    {
        var existingCount = await _unitOfWork.AppSettings
            .GetDatasetCategoryCountAsync(cancellationToken)
            .ConfigureAwait(false);

        if (existingCount > 0)
            return;

        var defaultCategories = new[]
        {
            new DatasetCategory { Name = "Character", Order = 0, IsDefault = true, AppSettingsId = 1 },
            new DatasetCategory { Name = "Style", Order = 1, IsDefault = true, AppSettingsId = 1 },
            new DatasetCategory { Name = "Concept", Order = 2, IsDefault = true, AppSettingsId = 1 }
        };

        await _unitOfWork.AppSettings
            .AddDatasetCategoriesAsync(defaultCategories, cancellationToken)
            .ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        settings.UpdatedAt = DateTimeOffset.UtcNow;

        // Update ordering and FK assignments
        var order = 0;
        foreach (var source in settings.LoraSources)
        {
            source.Order = order++;
            source.AppSettingsId = 1;
        }

        var categoryOrder = 0;
        foreach (var category in settings.DatasetCategories)
        {
            category.Order = categoryOrder++;
            category.AppSettingsId = 1;
        }

        var galleryOrder = 0;
        foreach (var gallery in settings.ImageGalleries)
        {
            gallery.Order = galleryOrder++;
            gallery.AppSettingsId = 1;
        }

        // Capture incoming data before any tracking queries
        var incomingSourceData = settings.LoraSources
            .Select(s => new { s.Id, s.FolderPath, s.IsEnabled, s.Order })
            .ToList();

        var incomingCategoryData = settings.DatasetCategories
            .Select(c => new { c.Id, c.Name, c.Description, c.IsDefault, c.Order })
            .ToList();

        var incomingGalleryData = settings.ImageGalleries
            .Select(g => new { g.Id, g.FolderPath, g.IsEnabled, g.Order })
            .ToList();

        var existingSettings = await _unitOfWork.AppSettings
            .GetSettingsWithIncludesAsync(cancellationToken)
            .ConfigureAwait(false);

        // Update scalar properties
        existingSettings.EncryptedCivitaiApiKey = settings.EncryptedCivitaiApiKey;
        existingSettings.ShowNsfw = settings.ShowNsfw;
        existingSettings.GenerateVideoThumbnails = settings.GenerateVideoThumbnails;
        existingSettings.ShowVideoPreview = settings.ShowVideoPreview;
        existingSettings.UseForgeStylePrompts = settings.UseForgeStylePrompts;
        existingSettings.MergeLoraSources = settings.MergeLoraSources;
        existingSettings.LoraSortSourcePath = settings.LoraSortSourcePath;
        existingSettings.LoraSortTargetPath = settings.LoraSortTargetPath;
        existingSettings.DatasetStoragePath = settings.DatasetStoragePath;
        existingSettings.DeleteEmptySourceFolders = settings.DeleteEmptySourceFolders;
        existingSettings.AutoBackupEnabled = settings.AutoBackupEnabled;
        existingSettings.AutoBackupIntervalDays = settings.AutoBackupIntervalDays;
        existingSettings.AutoBackupIntervalHours = settings.AutoBackupIntervalHours;
        existingSettings.AutoBackupLocation = settings.AutoBackupLocation;
        existingSettings.MaxBackups = settings.MaxBackups;
        existingSettings.LastBackupAt = settings.LastBackupAt;
        existingSettings.ComfyUiServerUrl = settings.ComfyUiServerUrl;
        existingSettings.UpdatedAt = settings.UpdatedAt;

        // Handle LoRA sources (remove deleted, update existing, add new)
        SyncChildCollection(
            existingSettings.LoraSources,
            incomingSourceData,
            s => s.Id,
            d => d.Id,
            (existing, data) =>
            {
                existing.FolderPath = data.FolderPath;
                existing.IsEnabled = data.IsEnabled;
                existing.Order = data.Order;
            },
            data => new LoraSource
            {
                AppSettingsId = 1,
                FolderPath = data.FolderPath,
                IsEnabled = data.IsEnabled,
                Order = data.Order
            },
            _unitOfWork.AppSettings.RemoveLoraSource,
            async source => await _unitOfWork.AppSettings.AddLoraSourceAsync(source, cancellationToken).ConfigureAwait(false));

        // Handle DatasetCategories (remove deleted non-defaults, update existing, add new)
        var existingCategoryIds = existingSettings.DatasetCategories.Where(c => c.Id > 0).Select(c => c.Id).ToHashSet();
        var incomingCategoryIds = incomingCategoryData.Where(c => c.Id > 0).Select(c => c.Id).ToHashSet();

        foreach (var category in existingSettings.DatasetCategories.ToList())
        {
            if (category.Id > 0 && !category.IsDefault && !incomingCategoryIds.Contains(category.Id))
                _unitOfWork.AppSettings.RemoveDatasetCategory(category);
        }

        foreach (var categoryData in incomingCategoryData)
        {
            if (categoryData.Id > 0 && existingCategoryIds.Contains(categoryData.Id))
            {
                var existingCategory = existingSettings.DatasetCategories.First(c => c.Id == categoryData.Id);
                if (!existingCategory.IsDefault)
                    existingCategory.Name = categoryData.Name;
                existingCategory.Description = categoryData.Description;
                existingCategory.Order = categoryData.Order;
            }
            else if (categoryData.Id == 0)
            {
                await _unitOfWork.AppSettings.AddDatasetCategoriesAsync(
                    [new DatasetCategory
                    {
                        AppSettingsId = 1,
                        Name = categoryData.Name,
                        Description = categoryData.Description,
                        IsDefault = false,
                        Order = categoryData.Order
                    }], cancellationToken).ConfigureAwait(false);
            }
        }

        // Handle ImageGalleries (remove deleted, update existing, add new)
        SyncChildCollection(
            existingSettings.ImageGalleries,
            incomingGalleryData,
            g => g.Id,
            d => d.Id,
            (existing, data) =>
            {
                existing.FolderPath = data.FolderPath;
                existing.IsEnabled = data.IsEnabled;
                existing.Order = data.Order;
            },
            data => new ImageGallery
            {
                AppSettingsId = 1,
                FolderPath = data.FolderPath,
                IsEnabled = data.IsEnabled,
                Order = data.Order
            },
            _unitOfWork.AppSettings.RemoveImageGallery,
            async gallery => await _unitOfWork.AppSettings.AddImageGalleryAsync(gallery, cancellationToken).ConfigureAwait(false));

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Synchronizes a child collection: removes deleted, updates existing, adds new.
    /// </summary>
    private static void SyncChildCollection<TEntity, TData>(
        ICollection<TEntity> existingCollection,
        IList<TData> incomingData,
        Func<TEntity, int> entityIdSelector,
        Func<TData, int> dataIdSelector,
        Action<TEntity, TData> updateAction,
        Func<TData, TEntity> createAction,
        Action<TEntity> removeAction,
        Func<TEntity, Task> addAction)
    {
        var existingIds = existingCollection.Where(e => entityIdSelector(e) > 0).Select(entityIdSelector).ToHashSet();
        var incomingIds = incomingData.Where(d => dataIdSelector(d) > 0).Select(dataIdSelector).ToHashSet();

        foreach (var entity in existingCollection.ToList())
        {
            if (entityIdSelector(entity) > 0 && !incomingIds.Contains(entityIdSelector(entity)))
                removeAction(entity);
        }

        foreach (var data in incomingData)
        {
            if (dataIdSelector(data) > 0 && existingIds.Contains(dataIdSelector(data)))
            {
                var existing = existingCollection.First(e => entityIdSelector(e) == dataIdSelector(data));
                updateAction(existing, data);
            }
            else if (dataIdSelector(data) == 0)
            {
                var newEntity = createAction(data);
                addAction(newEntity).GetAwaiter().GetResult();
            }
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetCivitaiApiKeyAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        return _secureStorage.Decrypt(settings.EncryptedCivitaiApiKey);
    }

    /// <inheritdoc />
    public async Task SetCivitaiApiKeyAsync(string? apiKey, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        settings.EncryptedCivitaiApiKey = string.IsNullOrWhiteSpace(apiKey)
            ? null
            : _secureStorage.Encrypt(apiKey);
        settings.UpdatedAt = DateTimeOffset.UtcNow;

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetEnabledLoraSourcesAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        return settings.LoraSources
            .Where(s => s.IsEnabled && !string.IsNullOrWhiteSpace(s.FolderPath))
            .OrderBy(s => s.Order)
            .Select(s => s.FolderPath)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<LoraSource> AddLoraSourceAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);

        var maxOrder = settings.LoraSources.Any()
            ? settings.LoraSources.Max(s => s.Order)
            : -1;

        var source = new LoraSource
        {
            AppSettingsId = 1,
            FolderPath = folderPath,
            IsEnabled = true,
            Order = maxOrder + 1
        };

        await _unitOfWork.AppSettings.AddLoraSourceAsync(source, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return source;
    }

    /// <inheritdoc />
    public async Task RemoveLoraSourceAsync(int sourceId, CancellationToken cancellationToken = default)
    {
        var source = await _unitOfWork.AppSettings
            .FindLoraSourceByIdAsync(sourceId, cancellationToken)
            .ConfigureAwait(false);

        if (source is not null)
        {
            _unitOfWork.AppSettings.RemoveLoraSource(source);
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task UpdateLoraSourceAsync(LoraSource source, CancellationToken cancellationToken = default)
    {
        var existingSource = await _unitOfWork.AppSettings
            .FindLoraSourceByIdAsync(source.Id, cancellationToken)
            .ConfigureAwait(false);

        if (existingSource is not null)
        {
            existingSource.FolderPath = source.FolderPath;
            existingSource.IsEnabled = source.IsEnabled;
            existingSource.Order = source.Order;

            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task UpdateLastBackupAtAsync(DateTimeOffset lastBackupAt, CancellationToken cancellationToken = default)
    {
        var settings = await _unitOfWork.AppSettings
            .GetSettingsAsync(cancellationToken)
            .ConfigureAwait(false);

        if (settings is not null)
        {
            settings.LastBackupAt = lastBackupAt;
            settings.UpdatedAt = DateTimeOffset.UtcNow;
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
