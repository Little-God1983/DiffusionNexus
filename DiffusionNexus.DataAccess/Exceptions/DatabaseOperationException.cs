namespace DiffusionNexus.DataAccess.Exceptions;

/// <summary>
/// Wraps EF Core / SQLite exceptions that occur during database operations,
/// providing additional context about the failed operation.
/// </summary>
public sealed class DatabaseOperationException : Exception
{
    /// <summary>
    /// The operation that was being performed when the error occurred.
    /// </summary>
    public string Operation { get; }

    public DatabaseOperationException(string operation, string message)
        : base(message)
    {
        Operation = operation;
    }

    public DatabaseOperationException(string operation, string message, Exception innerException)
        : base(message, innerException)
    {
        Operation = operation;
    }
}
