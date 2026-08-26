using DiffusionNexus.DataAccess.Exceptions;
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
        // Each consumer holds this service (and its context) for the app lifetime,
        // while other components delete the same settings rows through their own
        // contexts. EF's identity map never evicts those deletions, so without a
        // reset every re-read returns phantom children — they reappear in the UI,
        // keep being scanned, and poison the next save with a 0-row DELETE/UPDATE.
        _unitOfWork.ClearChangeTracker();

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

        settings = await RemoveDuplicateFolderRowsAsync(settings, cancellationToken).ConfigureAwait(false);

        return settings;
    }

    /// <summary>
    /// Prunes folder rows that sit duplicated in the database (historical concurrent
    /// startup saves) from the Generation Gallery, LoRA source, and Base Model Folder
    /// lists — same self-healing idea as <see cref="RemoveDuplicateCategoriesAsync"/>.
    /// The row worth keeping wins: ⭐ default, then installer-linked, then persisted.
    /// </summary>
    private async Task<AppSettings> RemoveDuplicateFolderRowsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var removed =
            PruneDuplicates(settings.LoraSources, s => s.FolderPath,
                s => (s.Id > 0 ? 2 : 0) + (s.IsEnabled ? 1 : 0),
                _unitOfWork.AppSettings.RemoveLoraSource)
            + PruneDuplicates(settings.ImageGalleries, g => g.FolderPath,
                g => (g.InstallerPackageId is not null ? 4 : 0) + (g.Id > 0 ? 2 : 0) + (g.IsEnabled ? 1 : 0),
                _unitOfWork.AppSettings.RemoveImageGallery)
            + PruneDuplicates(settings.BaseModelFolders, f => f.FolderPath,
                f => (f.IsDefault ? 8 : 0) + (f.InstallerPackageId is not null ? 4 : 0) + (f.Id > 0 ? 2 : 0) + (f.IsEnabled ? 1 : 0),
                _unitOfWork.AppSettings.RemoveBaseModelFolder);

        if (removed == 0)
        {
            return settings;
        }

        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return settings;
        }
        catch (ConcurrencyConflictException)
        {
            // Another context pruned the same duplicates first (0-row DELETE).
            // Discard the poisoned entries and hand back a fresh read instead.
            _unitOfWork.ClearChangeTracker();
            return await _unitOfWork.AppSettings
                .GetSettingsWithIncludesAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Removes all but the highest-scoring row per normalized folder path from both
    /// the navigation collection and the context. Blank paths are left alone.
    /// </summary>
    private static int PruneDuplicates<T>(
        ICollection<T> rows,
        Func<T, string?> pathOf,
        Func<T, int> keepScore,
        Action<T> removeAction)
    {
        var duplicates = rows
            .GroupBy(r => NormalizeFolderKey(pathOf(r)), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0 && g.Count() > 1)
            .SelectMany(g => g.OrderByDescending(keepScore).Skip(1))
            .ToList();

        foreach (var duplicate in duplicates)
        {
            removeAction(duplicate);
            rows.Remove(duplicate);
        }

        return duplicates.Count;
    }

    /// <summary>
    /// Keeps only the highest-scoring entry per normalized folder path, preserving
    /// the original order of the survivors. Blank paths are never merged.
    /// </summary>
    private static List<T> DedupeFolderRows<T>(List<T> rows, Func<T, string?> pathOf, Func<T, int> keepScore)
    {
        if (rows.Count < 2)
        {
            return rows;
        }

        return rows
            .Select((row, index) => (Row: row, Index: index))
            .GroupBy(x => NormalizeFolderKey(pathOf(x.Row)), StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g.Key.Length == 0
                ? g.AsEnumerable()
                : new[] { g.OrderByDescending(x => keepScore(x.Row)).ThenBy(x => x.Index).First() })
            .OrderBy(x => x.Index)
            .Select(x => x.Row)
            .ToList();
    }

    /// <summary>
    /// Comparison key for folder paths: full form when resolvable, trailing
    /// separators trimmed. Case-insensitive comparison is the caller's job.
    /// </summary>
    private static string NormalizeFolderKey(string? path)
    {
        var trimmed = path?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            trimmed = Path.GetFullPath(trimmed);
        }
        catch (Exception)
        {
            // Invalid path characters — compare the raw string instead.
        }

        return Path.TrimEndingDirectorySeparator(trimmed);
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

        var folderOrder = 0;
        foreach (var folder in settings.BaseModelFolders)
        {
            folder.Order = folderOrder++;
            folder.AppSettingsId = 1;
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

        var incomingFolderData = settings.BaseModelFolders
            .Select(f => new { f.Id, f.FolderPath, f.IsEnabled, f.Order, f.IsDefault, f.InstallerPackageId })
            .ToList();

        // A folder must never appear twice in a list (case-insensitive, trailing
        // separators ignored). Persisted rows outrank new ones so their FK links
        // survive; the ⭐ default outranks everything.
        incomingSourceData = DedupeFolderRows(incomingSourceData, d => d.FolderPath, d => d.Id > 0 ? 1 : 0);
        incomingGalleryData = DedupeFolderRows(incomingGalleryData, d => d.FolderPath, d => d.Id > 0 ? 1 : 0);
        incomingFolderData = DedupeFolderRows(incomingFolderData, d => d.FolderPath,
            d => (d.IsDefault ? 4 : 0) + (d.InstallerPackageId is not null ? 2 : 0) + (d.Id > 0 ? 1 : 0));

        // Sync against database truth, not this context's identity map — rows
        // deleted by another context would otherwise still sit in the tracked
        // graph and turn the save into a failing 0-row DELETE/UPDATE.
        _unitOfWork.ClearChangeTracker();

        var existingSettings = await _unitOfWork.AppSettings
            .GetSettingsWithIncludesAsync(cancellationToken)
            .ConfigureAwait(false);

        // Update scalar properties
        existingSettings.EncryptedCivitaiApiKey = settings.EncryptedCivitaiApiKey;
        existingSettings.EncryptedHuggingfaceApiKey = settings.EncryptedHuggingfaceApiKey;
        existingSettings.ShowNsfw = settings.ShowNsfw;
        existingSettings.GenerateVideoThumbnails = settings.GenerateVideoThumbnails;
        existingSettings.ShowVideoPreview = settings.ShowVideoPreview;
        existingSettings.UseForgeStylePrompts = settings.UseForgeStylePrompts;
        existingSettings.MergeLoraSources = settings.MergeLoraSources;
        existingSettings.LoraSortSourcePath = settings.LoraSortSourcePath;
        existingSettings.LoraSortTargetPath = settings.LoraSortTargetPath;
        existingSettings.DatasetStoragePath = settings.DatasetStoragePath;
        existingSettings.DeleteEmptySourceFolders = settings.DeleteEmptySourceFolders;
        existingSettings.BackupDatasetImagesEnabled = settings.BackupDatasetImagesEnabled;
        existingSettings.BackupDatabaseEnabled = settings.BackupDatabaseEnabled;
        existingSettings.AutoBackupIntervalDays = settings.AutoBackupIntervalDays;
        existingSettings.AutoBackupIntervalHours = settings.AutoBackupIntervalHours;
        existingSettings.AutoBackupLocation = settings.AutoBackupLocation;
        existingSettings.MaxBackups = settings.MaxBackups;
        existingSettings.LastBackupAt = settings.LastBackupAt;
        existingSettings.ComfyUiServerUrl = settings.ComfyUiServerUrl;
        existingSettings.LoraUpdateCheckStalenessDays = settings.LoraUpdateCheckStalenessDays;
        existingSettings.SyncNotIdentifiedRetryDays = settings.SyncNotIdentifiedRetryDays;
        existingSettings.SyncErrorRetryDays = settings.SyncErrorRetryDays;
        existingSettings.SyncThumbnailConcurrency = settings.SyncThumbnailConcurrency;
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

        // Handle BaseModelFolders (remove deleted, update existing, add new)
        SyncChildCollection(
            existingSettings.BaseModelFolders,
            incomingFolderData,
            f => f.Id,
            d => d.Id,
            (existing, data) =>
            {
                existing.FolderPath = data.FolderPath;
                existing.IsEnabled = data.IsEnabled;
                existing.Order = data.Order;
                existing.IsDefault = data.IsDefault;
                existing.InstallerPackageId = data.InstallerPackageId;
            },
            data => new BaseModelFolder
            {
                AppSettingsId = 1,
                FolderPath = data.FolderPath,
                IsEnabled = data.IsEnabled,
                Order = data.Order,
                IsDefault = data.IsDefault,
                InstallerPackageId = data.InstallerPackageId
            },
            _unitOfWork.AppSettings.RemoveBaseModelFolder,
            async folder => await _unitOfWork.AppSettings.AddBaseModelFolderAsync(folder, cancellationToken).ConfigureAwait(false));

        // Single-default invariant: at most one Base Model Folder may be the default.
        // When several incoming rows are flagged, the last one (incoming order) wins.
        var lastDefaultPath = incomingFolderData.LastOrDefault(d => d.IsDefault)?.FolderPath;
        foreach (var folder in existingSettings.BaseModelFolders.Where(f => f.IsDefault))
        {
            if (!string.Equals(folder.FolderPath, lastDefaultPath, StringComparison.OrdinalIgnoreCase))
                folder.IsDefault = false;
        }

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
    public async Task<string?> GetHuggingfaceApiKeyAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        return _secureStorage.Decrypt(settings.EncryptedHuggingfaceApiKey);
    }

    /// <inheritdoc />
    public async Task SetHuggingfaceApiKeyAsync(string? token, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        settings.EncryptedHuggingfaceApiKey = string.IsNullOrWhiteSpace(token)
            ? null
            : _secureStorage.Encrypt(token);
        settings.UpdatedAt = DateTimeOffset.UtcNow;

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BaseModelFolder>> GetEnabledBaseModelFoldersAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        return settings.BaseModelFolders
            .Where(f => f.IsEnabled && !string.IsNullOrWhiteSpace(f.FolderPath))
            .OrderBy(f => f.Order)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BaseModelFolder>> GetAllBaseModelFoldersAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        return settings.BaseModelFolders
            .OrderBy(f => f.Order)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<int> RemoveBaseModelFoldersAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return 0;
        }

        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);

        // The default row is protected here rather than only at the call site: this is the
        // last place before the delete, and a lost download target is a silent failure.
        var doomed = settings.BaseModelFolders
            .Where(f => ids.Contains(f.Id) && !f.IsDefault)
            .ToList();

        if (doomed.Count == 0)
        {
            return 0;
        }

        foreach (var folder in doomed)
        {
            settings.BaseModelFolders.Remove(folder);
            _unitOfWork.AppSettings.RemoveBaseModelFolder(folder);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return doomed.Count;
    }

    /// <inheritdoc />
    public async Task<bool> AddBaseModelFolderAsync(string folderPath, int? installerPackageId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);

        var existing = settings.BaseModelFolders.FirstOrDefault(f =>
            string.Equals(f.FolderPath, folderPath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            if (installerPackageId is not null && existing.InstallerPackageId != installerPackageId)
            {
                existing.InstallerPackageId = installerPackageId;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            return false;
        }

        var maxOrder = settings.BaseModelFolders.Any()
            ? settings.BaseModelFolders.Max(f => f.Order)
            : -1;

        await _unitOfWork.AppSettings.AddBaseModelFolderAsync(new BaseModelFolder
        {
            AppSettingsId = settings.Id,
            FolderPath = folderPath,
            IsEnabled = true,
            IsDefault = false,
            Order = maxOrder + 1,
            InstallerPackageId = installerPackageId
        }, cancellationToken).ConfigureAwait(false);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
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
    public async Task<string?> GetFavoriteLoraSourceAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _unitOfWork.AppSettings
            .GetSettingsAsync(cancellationToken)
            .ConfigureAwait(false);
        return settings?.FavoriteLoraSourcePath;
    }

    /// <inheritdoc />
    public async Task SetFavoriteLoraSourceAsync(string? folderPath, CancellationToken cancellationToken = default)
    {
        var settings = await _unitOfWork.AppSettings
            .GetSettingsAsync(cancellationToken)
            .ConfigureAwait(false);
        if (settings is null) return;

        // Normalize empty string to null so "no favorite" is unambiguous.
        settings.FavoriteLoraSourcePath = string.IsNullOrWhiteSpace(folderPath) ? null : folderPath;
        settings.UpdatedAt = DateTimeOffset.UtcNow;
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string?> GetFeedbackReporterEmailAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _unitOfWork.AppSettings
            .GetSettingsAsync(cancellationToken)
            .ConfigureAwait(false);
        return settings?.FeedbackReporterEmail;
    }

    /// <inheritdoc />
    public async Task SetFeedbackReporterEmailAsync(string? email, CancellationToken cancellationToken = default)
    {
        var settings = await _unitOfWork.AppSettings
            .GetSettingsAsync(cancellationToken)
            .ConfigureAwait(false);
        if (settings is null) return;

        settings.FeedbackReporterEmail = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        settings.UpdatedAt = DateTimeOffset.UtcNow;
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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

    /// <inheritdoc />
    public async Task UpdateLastLibrarySyncAtAsync(DateTimeOffset lastLibrarySyncAt, CancellationToken cancellationToken = default)
    {
        var settings = await _unitOfWork.AppSettings
            .GetSettingsAsync(cancellationToken)
            .ConfigureAwait(false);

        if (settings is not null)
        {
            settings.LastLibrarySyncAt = lastLibrarySyncAt;
            settings.UpdatedAt = DateTimeOffset.UtcNow;
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetLoraViewerFilterJsonAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        return settings.LoraViewerFilterJson;
    }

    /// <inheritdoc />
    public async Task SetLoraViewerFilterJsonAsync(string? json, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        settings.LoraViewerFilterJson = string.IsNullOrWhiteSpace(json) ? null : json;
        settings.UpdatedAt = DateTimeOffset.UtcNow;
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
