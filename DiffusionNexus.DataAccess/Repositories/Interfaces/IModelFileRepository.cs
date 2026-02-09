using DiffusionNexus.Domain.Entities;

namespace DiffusionNexus.DataAccess.Repositories.Interfaces;

/// <summary>
/// Repository for <see cref="ModelFile"/> entities with domain-specific query methods.
/// </summary>
public interface IModelFileRepository : IRepository<ModelFile>
{
    /// <summary>
    /// Gets all model files that have a local path set.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Model files with local paths.</returns>
    Task<IReadOnlyList<ModelFile>> GetAllWithLocalPathAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all local paths as a hash set for fast lookup.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Hash set of existing local file paths (case-insensitive).</returns>
    Task<HashSet<string>> GetExistingLocalPathsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds model files matching a specific file size that have an invalid local path,
    /// used for detecting moved files.
    /// </summary>
    /// <param name="fileSize">Exact file size in bytes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Candidate model files for hash comparison.</returns>
    Task<IReadOnlyList<ModelFile>> FindBySizeWithInvalidPathAsync(long fileSize, CancellationToken cancellationToken = default);
}
