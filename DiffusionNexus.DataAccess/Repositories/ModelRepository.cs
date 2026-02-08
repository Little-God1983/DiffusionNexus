using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DiffusionNexus.DataAccess.Repositories;

/// <summary>
/// Repository for <see cref="Model"/> entities.
/// </summary>
internal sealed class ModelRepository : RepositoryBase<Model>, IModelRepository
{
    public ModelRepository(DiffusionNexusCoreDbContext context) : base(context)
    {
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Model>> GetModelsWithLocalFilesAsync(CancellationToken cancellationToken = default)
    {
        var models = await Context.Models
            .Include(m => m.Creator)
            .Include(m => m.Versions)
                .ThenInclude(v => v.Files)
            .Include(m => m.Versions)
                .ThenInclude(v => v.Images)
            .Include(m => m.Versions)
                .ThenInclude(v => v.TriggerWords)
            .AsSplitQuery()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return models
            .Where(m => m.Versions.Any(v => v.Files.Any(f => !string.IsNullOrEmpty(f.LocalPath))))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Model>> GetAllWithIncludesAsync(CancellationToken cancellationToken = default)
    {
        return await Context.Models
            .Include(m => m.Creator)
            .Include(m => m.Versions)
                .ThenInclude(v => v.Files)
            .Include(m => m.Versions)
                .ThenInclude(v => v.Images)
            .Include(m => m.Versions)
                .ThenInclude(v => v.TriggerWords)
            .AsSplitQuery()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
