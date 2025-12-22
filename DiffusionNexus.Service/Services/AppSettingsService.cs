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
            .FirstOrDefaultAsync(s => s.Id == 1, cancellationToken);

        if (settings is null)
        {
            // Create default settings
            settings = new AppSettings { Id = 1 };
            _dbContext.AppSettings.Add(settings);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return settings;
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
        }

        var existingSettings = await _dbContext.AppSettings
            .Include(s => s.LoraSources)
            .FirstOrDefaultAsync(s => s.Id == 1, cancellationToken);

        if (existingSettings is null)
        {
            settings.Id = 1;
            _dbContext.AppSettings.Add(settings);
        }
        else
        {
            // Update scalar properties
            _dbContext.Entry(existingSettings).CurrentValues.SetValues(settings);

            // Handle LoRA sources (remove deleted, update existing, add new)
            var existingSourceIds = existingSettings.LoraSources.Select(s => s.Id).ToHashSet();
            var newSourceIds = settings.LoraSources.Where(s => s.Id > 0).Select(s => s.Id).ToHashSet();

            // Remove deleted sources
            foreach (var source in existingSettings.LoraSources.ToList())
            {
                if (!newSourceIds.Contains(source.Id))
                {
                    _dbContext.LoraSources.Remove(source);
                }
            }

            // Update or add sources
            foreach (var source in settings.LoraSources)
            {
                source.AppSettingsId = 1;

                if (source.Id > 0 && existingSourceIds.Contains(source.Id))
                {
                    var existingSource = existingSettings.LoraSources.First(s => s.Id == source.Id);
                    _dbContext.Entry(existingSource).CurrentValues.SetValues(source);
                }
                else
                {
                    source.Id = 0; // Ensure new sources get auto-generated IDs
                    _dbContext.LoraSources.Add(source);
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
