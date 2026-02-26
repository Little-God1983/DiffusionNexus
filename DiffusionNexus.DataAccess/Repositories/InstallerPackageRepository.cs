using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DiffusionNexus.DataAccess.Repositories;

/// <summary>
/// Repository for <see cref="InstallerPackage"/> entities.
/// </summary>
internal sealed class InstallerPackageRepository : RepositoryBase<InstallerPackage>, IInstallerPackageRepository
{
    public InstallerPackageRepository(DiffusionNexusCoreDbContext context) : base(context)
    {
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<InstallerPackage>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await DbSet
            .Include(p => p.ImageGallery)
            .OrderBy(p => p.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<InstallerPackage?> GetByIdWithGalleryAsync(int id, CancellationToken cancellationToken = default)
    {
        return await DbSet
            .Include(p => p.ImageGallery)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ClearDefaultByTypeAsync(InstallerType type, CancellationToken cancellationToken = default)
    {
        await DbSet
            .Where(p => p.Type == type && p.IsDefault)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsDefault, false), cancellationToken)
            .ConfigureAwait(false);
    }
}
