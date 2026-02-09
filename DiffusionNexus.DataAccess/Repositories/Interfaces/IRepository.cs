namespace DiffusionNexus.DataAccess.Repositories.Interfaces;

/// <summary>
/// Full CRUD repository interface extending read-only capabilities.
/// </summary>
/// <typeparam name="T">The entity type.</typeparam>
public interface IRepository<T> : IReadOnlyRepository<T> where T : class
{
    /// <summary>
    /// Adds a new entity to the context.
    /// </summary>
    /// <param name="entity">The entity to add.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AddAsync(T entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a range of entities to the context.
    /// </summary>
    /// <param name="entities">The entities to add.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AddRangeAsync(IEnumerable<T> entities, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an entity as modified in the context.
    /// </summary>
    /// <param name="entity">The entity to update.</param>
    void Update(T entity);

    /// <summary>
    /// Marks an entity for removal from the context.
    /// </summary>
    /// <param name="entity">The entity to remove.</param>
    void Remove(T entity);

    /// <summary>
    /// Marks a range of entities for removal from the context.
    /// </summary>
    /// <param name="entities">The entities to remove.</param>
    void RemoveRange(IEnumerable<T> entities);
}
