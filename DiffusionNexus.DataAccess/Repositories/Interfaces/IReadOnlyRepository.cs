using System.Linq.Expressions;

namespace DiffusionNexus.DataAccess.Repositories.Interfaces;

/// <summary>
/// Read-only repository interface for querying entities without modification.
/// </summary>
/// <typeparam name="T">The entity type.</typeparam>
public interface IReadOnlyRepository<T> where T : class
{
    /// <summary>
    /// Gets an entity by its primary key.
    /// </summary>
    /// <param name="id">The primary key value.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The entity, or null if not found.</returns>
    Task<T?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all entities of this type.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All entities.</returns>
    Task<IReadOnlyList<T>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds entities matching the given predicate.
    /// </summary>
    /// <param name="predicate">The filter expression.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matching entities.</returns>
    Task<IReadOnlyList<T>> FindAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether any entity matches the given predicate.
    /// </summary>
    /// <param name="predicate">The filter expression.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if at least one entity matches.</returns>
    Task<bool> AnyAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts entities matching the given predicate.
    /// </summary>
    /// <param name="predicate">The filter expression.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The count of matching entities.</returns>
    Task<int> CountAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);
}
