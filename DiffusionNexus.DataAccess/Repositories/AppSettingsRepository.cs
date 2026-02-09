using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DiffusionNexus.DataAccess.Repositories;

/// <summary>
/// Repository for <see cref="AppSettings"/> and its child collections.
/// </summary>
internal sealed class AppSettingsRepository : RepositoryBase<AppSettings>, IAppSettingsRepository
{
    public AppSettingsRepository(DiffusionNexusCoreDbContext context) : base(context)
    {
    }

    /// <inheritdoc />
    public async Task<AppSettings> GetSettingsWithIncludesAsync(CancellationToken cancellationToken = default)
    {
        var settings = await DbSet
            .Include(s => s.LoraSources.OrderBy(ls => ls.Order))
            .Include(s => s.DatasetCategories.OrderBy(c => c.Order))
            .Include(s => s.ImageGalleries.OrderBy(g => g.Order))
            .FirstOrDefaultAsync(s => s.Id == 1, cancellationToken)
            .ConfigureAwait(false);

        if (settings is not null)
            return settings;

        settings = new AppSettings { Id = 1 };
        await DbSet.AddAsync(settings, cancellationToken).ConfigureAwait(false);
        return settings;
    }

    /// <inheritdoc />
    public async Task<AppSettings?> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        return await DbSet
            .FirstOrDefaultAsync(s => s.Id == 1, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> GetDatasetCategoryCountAsync(CancellationToken cancellationToken = default)
    {
        return await Context.DatasetCategories
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddDatasetCategoriesAsync(
        IEnumerable<DatasetCategory> categories,
        CancellationToken cancellationToken = default)
    {
        await Context.DatasetCategories
            .AddRangeAsync(categories, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void RemoveDatasetCategory(DatasetCategory category)
    {
        Context.DatasetCategories.Remove(category);
    }

    /// <inheritdoc />
    public async Task AddLoraSourceAsync(LoraSource source, CancellationToken cancellationToken = default)
    {
        await Context.LoraSources
            .AddAsync(source, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void RemoveLoraSource(LoraSource source)
    {
        Context.LoraSources.Remove(source);
    }

    /// <inheritdoc />
    public async Task<LoraSource?> FindLoraSourceByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await Context.LoraSources
            .FindAsync([id], cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddImageGalleryAsync(ImageGallery gallery, CancellationToken cancellationToken = default)
    {
        await Context.ImageGalleries
            .AddAsync(gallery, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void RemoveImageGallery(ImageGallery gallery)
    {
        Context.ImageGalleries.Remove(gallery);
    }
}
