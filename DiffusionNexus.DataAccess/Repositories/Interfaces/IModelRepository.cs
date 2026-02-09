using DiffusionNexus.Domain.Entities;

namespace DiffusionNexus.DataAccess.Repositories.Interfaces;

/// <summary>
/// Repository for <see cref="Model"/> entities with domain-specific query methods.
/// </summary>
public interface IModelRepository : IRepository<Model>
{
    /// <summary>
    /// Loads all models with their full navigation graph (Versions, Files, Images, TriggerWords, Creator)
    /// that have at least one file with a non-empty local path.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Models with local files, fully populated.</returns>
    Task<IReadOnlyList<Model>> GetModelsWithLocalFilesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads all models with their full navigation graph using a split query for performance.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All models with related entities.</returns>
    Task<IReadOnlyList<Model>> GetAllWithIncludesAsync(CancellationToken cancellationToken = default);
}
