using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.Domain.Entities;
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
    public async Task<IReadOnlyList<InstallerPackage>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await DbSet
            .OrderBy(p => p.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
