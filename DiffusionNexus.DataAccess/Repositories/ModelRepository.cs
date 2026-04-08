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
        return await Context.Models
            .Include(m => m.Creator)
            .Include(m => m.Tags)
                .ThenInclude(mt => mt.Tag)
            .Include(m => m.Versions)
                .ThenInclude(v => v.Files)
            .Include(m => m.Versions)
                .ThenInclude(v => v.Images)
            .Include(m => m.Versions)
                .ThenInclude(v => v.TriggerWords)
            .Where(m => m.Versions.Any(v => v.Files.Any(f => f.LocalPath != null && f.LocalPath != "")))
            .AsSplitQuery()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Model>> GetAllWithIncludesAsync(CancellationToken cancellationToken = default)
    {
        return await Context.Models
            .Include(m => m.Creator)
            .Include(m => m.Tags)
                .ThenInclude(mt => mt.Tag)
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

    /// <inheritdoc />
    public async Task<Model?> GetByIdWithIncludesAsync(int id, CancellationToken cancellationToken = default)
    {
        return await Context.Models
            .Include(m => m.Creator)
            .Include(m => m.Tags)
                .ThenInclude(mt => mt.Tag)
            .Include(m => m.Versions)
                .ThenInclude(v => v.Files)
            .Include(m => m.Versions)
                .ThenInclude(v => v.Images)
            .Include(m => m.Versions)
                .ThenInclude(v => v.TriggerWords)
            .AsSplitQuery()
            .FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> IsCivitaiIdTakenAsync(int civitaiId, int excludeModelId, CancellationToken cancellationToken = default)
    {
        return await Context.Models
            .AnyAsync(m => m.CivitaiId == civitaiId && m.Id != excludeModelId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> IsVersionCivitaiIdTakenAsync(int civitaiVersionId, int excludeVersionId, CancellationToken cancellationToken = default)
    {
        return await Context.ModelVersions
            .AnyAsync(v => v.CivitaiId == civitaiVersionId && v.Id != excludeVersionId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Creator?> FindCreatorByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        return await Context.Creators
            .FirstOrDefaultAsync(c => c.Username.ToLower() == username.ToLower(), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, Tag>> GetAllTagsLookupAsync(CancellationToken cancellationToken = default)
    {
        return await Context.Tags
            .ToDictionaryAsync(t => t.NormalizedName, StringComparer.OrdinalIgnoreCase, cancellationToken)
            .ConfigureAwait(false);
    }
}
