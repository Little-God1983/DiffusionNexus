namespace DiffusionNexus.DataAccess.Exceptions;

/// <summary>
/// Thrown when a concurrency conflict is detected during a database update
/// (e.g., another process modified the same row).
/// </summary>
public sealed class ConcurrencyConflictException : Exception
{
    /// <summary>
    /// The type of the entity involved in the conflict.
    /// </summary>
    public string EntityType { get; }

    public ConcurrencyConflictException(string entityType)
        : base($"A concurrency conflict was detected for entity '{entityType}'.")
    {
        EntityType = entityType;
    }

    public ConcurrencyConflictException(string entityType, Exception innerException)
        : base($"A concurrency conflict was detected for entity '{entityType}'.", innerException)
    {
        EntityType = entityType;
    }
}
