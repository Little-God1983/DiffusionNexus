using DiffusionNexus.Domain.Entities;

namespace DiffusionNexus.DataAccess.Repositories.Interfaces;

/// <summary>
/// Repository for <see cref="InstallerPackage"/> entities.
/// </summary>
public interface IInstallerPackageRepository : IRepository<InstallerPackage>
{
    /// <summary>
    /// Gets all installer packages ordered by name, including linked ImageGallery.
    /// </summary>
    Task<IReadOnlyList<InstallerPackage>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets an installer package by ID with its linked ImageGallery loaded.
    /// </summary>
    Task<InstallerPackage?> GetByIdWithGalleryAsync(int id, CancellationToken cancellationToken = default);
}
