using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Service for managing application settings stored in the database.
/// </summary>
public sealed class AppSettingsService : IAppSettingsService
{
    private readonly DiffusionNexusCoreDbContext _dbContext;
    private readonly ISecureStorage _secureStorage;

    public AppSettingsService(DiffusionNexusCoreDbContext dbContext, ISecureStorage secureStorage)
    {
        _dbContext = dbContext;
        _secureStorage = secureStorage;
    }

    /// <inheritdoc />
    public async Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _dbContext.AppSettings
            .Include(s => s.LoraSources.OrderBy(ls => ls.Order))
            .Include(s => s.DatasetCategories.OrderBy(c => c.Order))
            .FirstOrDefaultAsync(s => s.Id == 1, cancellationToken);

        if (settings is null)
        {
            // Create default settings with default categories
            settings = new AppSettings { Id = 1 };
            _dbContext.AppSettings.Add(settings);
            await _dbContext.SaveChangesAsync(cancellationToken);

            // Seed default categories
            await SeedDefaultCategoriesAsync(cancellationToken);

            // Reload to get the categories
            settings = await _dbContext.AppSettings
                .Include(s => s.LoraSources.OrderBy(ls => ls.Order))
                .Include(s => s.DatasetCategories.OrderBy(c => c.Order))
                .FirstAsync(s => s.Id == 1, cancellationToken);
        }
        else if (!settings.DatasetCategories.Any())
        {
            // Seed default categories if none exist
            await SeedDefaultCategoriesAsync(cancellationToken);

            // Reload to get the categories
            settings = await _dbContext.AppSettings
                .Include(s => s.LoraSources.OrderBy(ls => ls.Order))
                .Include(s => s.DatasetCategories.OrderBy(c => c.Order))
                .FirstAsync(s => s.Id == 1, cancellationToken);
        }

        return settings;
    }

    private async Task SeedDefaultCategoriesAsync(CancellationToken cancellationToken = default)
    {
        // Check if any categories already exist to avoid duplicates
        var existingCount = await _dbContext.DatasetCategories.CountAsync(cancellationToken);
        if (existingCount > 0)
        {
            return;
        }

        var defaultCategories = new[]
        {
            new DatasetCategory { Name = "Character", Order = 0, IsDefault = true, AppSettingsId = 1 },
            new DatasetCategory { Name = "Style", Order = 1, IsDefault = true, AppSettingsId = 1 },
            new DatasetCategory { Name = "Concept", Order = 2, IsDefault = true, AppSettingsId = 1 }
        };

        _dbContext.DatasetCategories.AddRange(defaultCategories);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        settings.UpdatedAt = DateTimeOffset.UtcNow;

        // Update LoRA source order
        var order = 0;
        foreach (var source in settings.LoraSources)
        {
            source.Order = order++;
            source.AppSettingsId = 1; // Ensure FK is set
        }

        // Update category order
        var categoryOrder = 0;
        foreach (var category in settings.DatasetCategories)
        {
            category.Order = categoryOrder++;
            category.AppSettingsId = 1; // Ensure FK is set
        }

        // Get the incoming data before querying (to avoid tracking issues)
        var incomingSourceData = settings.LoraSources
            .Select(s => new { s.Id, s.FolderPath, s.IsEnabled, s.Order })
            .ToList();

        var incomingCategoryData = settings.DatasetCategories
            .Select(c => new { c.Id, c.Name, c.Description, c.IsDefault, c.Order })
            .ToList();

        // Detach any tracked entities to avoid conflicts
        var trackedLoraSources = _dbContext.ChangeTracker.Entries<LoraSource>().ToList();
        foreach (var entry in trackedLoraSources)
        {
            entry.State = EntityState.Detached;
        }

        var trackedCategories = _dbContext.ChangeTracker.Entries<DatasetCategory>().ToList();
        foreach (var entry in trackedCategories)
        {
            entry.State = EntityState.Detached;
        }

        var existingSettings = await _dbContext.AppSettings
            .Include(s => s.LoraSources)
            .Include(s => s.DatasetCategories)
            .FirstOrDefaultAsync(s => s.Id == 1, cancellationToken);

        if (existingSettings is null)
        {
            settings.Id = 1;
            // Re-create LoraSources from the captured data
            settings.LoraSources = incomingSourceData
                .Select(s => new LoraSource
                {
                    Id = 0,
                    AppSettingsId = 1,
                    FolderPath = s.FolderPath,
                    IsEnabled = s.IsEnabled,
                    Order = s.Order
                })
                .ToList();
            settings.DatasetCategories = incomingCategoryData
                .Select(c => new DatasetCategory
                {
                    Id = 0,
                    AppSettingsId = 1,
                    Name = c.Name,
                    Description = c.Description,
                    IsDefault = c.IsDefault,
                    Order = c.Order
                })
                .ToList();
            _dbContext.AppSettings.Add(settings);
        }
        else
        {
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
            existingSettings.UpdatedAt = settings.UpdatedAt;

            // Handle LoRA sources (remove deleted, update existing, add new)
            var existingSourceIds = existingSettings.LoraSources
                .Where(s => s.Id > 0)
                .Select(s => s.Id)
                .ToHashSet();
            var incomingSourceIds = incomingSourceData
                .Where(s => s.Id > 0)
                .Select(s => s.Id)
                .ToHashSet();

            foreach (var source in existingSettings.LoraSources.ToList())
            {
                if (source.Id > 0 && !incomingSourceIds.Contains(source.Id))
                {
                    _dbContext.LoraSources.Remove(source);
                }
            }

            foreach (var sourceData in incomingSourceData)
            {
                if (sourceData.Id > 0 && existingSourceIds.Contains(sourceData.Id))
                {
                    var existingSource = existingSettings.LoraSources.First(s => s.Id == sourceData.Id);
                    existingSource.FolderPath = sourceData.FolderPath;
                    existingSource.IsEnabled = sourceData.IsEnabled;
                    existingSource.Order = sourceData.Order;
                }
                else
                {
                    var newSource = new LoraSource
                    {
                        AppSettingsId = 1,
                        FolderPath = sourceData.FolderPath,
                        IsEnabled = sourceData.IsEnabled,
                        Order = sourceData.Order
                    };
                    _dbContext.LoraSources.Add(newSource);
                }
            }

            // Handle DatasetCategories (remove deleted non-defaults, update existing, add new)
            var existingCategoryIds = existingSettings.DatasetCategories
                .Where(c => c.Id > 0)
                .Select(c => c.Id)
                .ToHashSet();
            var incomingCategoryIds = incomingCategoryData
                .Where(c => c.Id > 0)
                .Select(c => c.Id)
                .ToHashSet();

            foreach (var category in existingSettings.DatasetCategories.ToList())
            {
                // Only remove non-default categories that are not in incoming data
                if (category.Id > 0 && !category.IsDefault && !incomingCategoryIds.Contains(category.Id))
                {
                    _dbContext.DatasetCategories.Remove(category);
                }
            }

            foreach (var categoryData in incomingCategoryData)
            {
                if (categoryData.Id > 0 && existingCategoryIds.Contains(categoryData.Id))
                {
                    var existingCategory = existingSettings.DatasetCategories.First(c => c.Id == categoryData.Id);
                    // Don't update name for default categories
                    if (!existingCategory.IsDefault)
                    {
                        existingCategory.Name = categoryData.Name;
                    }
                    existingCategory.Description = categoryData.Description;
                    existingCategory.Order = categoryData.Order;
                }
                else if (categoryData.Id == 0)
                {
                    var newCategory = new DatasetCategory
                    {
                        AppSettingsId = 1,
                        Name = categoryData.Name,
                        Description = categoryData.Description,
                        IsDefault = false,
                        Order = categoryData.Order
                    };
                    _dbContext.DatasetCategories.Add(newCategory);
                }
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string?> GetCivitaiApiKeyAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        return _secureStorage.Decrypt(settings.EncryptedCivitaiApiKey);
    }

    /// <inheritdoc />
    public async Task SetCivitaiApiKeyAsync(string? apiKey, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        settings.EncryptedCivitaiApiKey = string.IsNullOrWhiteSpace(apiKey)
            ? null
            : _secureStorage.Encrypt(apiKey);
        settings.UpdatedAt = DateTimeOffset.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetEnabledLoraSourcesAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        return settings.LoraSources
            .Where(s => s.IsEnabled && !string.IsNullOrWhiteSpace(s.FolderPath))
            .OrderBy(s => s.Order)
            .Select(s => s.FolderPath)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<LoraSource> AddLoraSourceAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);

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

        _dbContext.LoraSources.Add(source);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return source;
    }

    /// <inheritdoc />
    public async Task RemoveLoraSourceAsync(int sourceId, CancellationToken cancellationToken = default)
    {
        var source = await _dbContext.LoraSources.FindAsync([sourceId], cancellationToken);
        if (source is not null)
        {
            _dbContext.LoraSources.Remove(source);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task UpdateLoraSourceAsync(LoraSource source, CancellationToken cancellationToken = default)
    {
        var existingSource = await _dbContext.LoraSources.FindAsync([source.Id], cancellationToken);
        if (existingSource is not null)
        {
            existingSource.FolderPath = source.FolderPath;
            existingSource.IsEnabled = source.IsEnabled;
            existingSource.Order = source.Order;

            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
