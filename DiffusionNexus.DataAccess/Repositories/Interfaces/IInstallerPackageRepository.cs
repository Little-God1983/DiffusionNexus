using DiffusionNexus.Domain.Entities;

namespace DiffusionNexus.DataAccess.Repositories.Interfaces;

/// <summary>
/// Repository for <see cref="InstallerPackage"/> entities.
/// </summary>
public interface IInstallerPackageRepository : IRepository<InstallerPackage>
{
    /// <summary>
    /// Gets all installer packages ordered by name.
    /// </summary>
    Task<IReadOnlyList<InstallerPackage>> GetAllAsync(CancellationToken cancellationToken = default);
}
