namespace DiffusionNexus.DataAccess.Exceptions;

/// <summary>
/// Thrown when an entity with the specified identifier is not found in the database.
/// </summary>
public sealed class EntityNotFoundException : Exception
{
    /// <summary>
    /// The type of the entity that was not found.
    /// </summary>
    public string EntityType { get; }

    /// <summary>
    /// The identifier that was searched for.
    /// </summary>
    public object EntityId { get; }

    public EntityNotFoundException(string entityType, object entityId)
        : base($"Entity '{entityType}' with id '{entityId}' was not found.")
    {
        EntityType = entityType;
        EntityId = entityId;
    }

    public EntityNotFoundException(string entityType, object entityId, Exception innerException)
        : base($"Entity '{entityType}' with id '{entityId}' was not found.", innerException)
    {
        EntityType = entityType;
        EntityId = entityId;
    }
}
