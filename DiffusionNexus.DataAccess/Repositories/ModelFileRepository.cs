using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DiffusionNexus.DataAccess.Repositories;

/// <summary>
/// Repository for <see cref="ModelFile"/> entities.
/// </summary>
internal sealed class ModelFileRepository : RepositoryBase<ModelFile>, IModelFileRepository
{
    public ModelFileRepository(DiffusionNexusCoreDbContext context) : base(context)
    {
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModelFile>> GetAllWithLocalPathAsync(CancellationToken cancellationToken = default)
    {
        return await DbSet
            .Where(f => f.LocalPath != null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<HashSet<string>> GetExistingLocalPathsAsync(CancellationToken cancellationToken = default)
    {
        var paths = await DbSet
            .Where(f => f.LocalPath != null)
            .Select(f => f.LocalPath!)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModelFile>> GetByLocalPathAsync(
        string localPath,
        CancellationToken cancellationToken = default)
    {
        // ToLower() (→ SQL LOWER()) rather than a collation so the comparison is
        // provider-portable; ASCII-only folding is fine for Windows drive paths.
        var normalized = localPath.ToLowerInvariant();
        return await DbSet
            .Include(f => f.ModelVersion)
            .Where(f => f.LocalPath != null && f.LocalPath.ToLower() == normalized)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModelFile>> FindBySizeWithInvalidPathAsync(
        long fileSize,
        CancellationToken cancellationToken = default)
    {
        return await DbSet
            .Where(f => f.FileSizeBytes == fileSize && f.LocalPath != null && !f.IsLocalFileValid)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
