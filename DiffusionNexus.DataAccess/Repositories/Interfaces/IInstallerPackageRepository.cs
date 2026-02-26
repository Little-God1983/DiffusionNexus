using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.DataAccess.Repositories.Interfaces;

/// <summary>
/// Repository for <see cref="InstallerPackage"/> entities.
/// </summary>
public interface IInstallerPackageRepository : IRepository<InstallerPackage>
{
    /// <summary>
    /// Gets an installer package by ID with its linked ImageGallery loaded.
    /// </summary>
    Task<InstallerPackage?> GetByIdWithGalleryAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the <see cref="InstallerPackage.IsDefault"/> flag for all packages of the given type.
    /// </summary>
    Task ClearDefaultByTypeAsync(InstallerType type, CancellationToken cancellationToken = default);
}
