using DiffusionNexus.Domain.Entities;

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
}
