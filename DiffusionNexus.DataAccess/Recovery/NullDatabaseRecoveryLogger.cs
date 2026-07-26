namespace DiffusionNexus.DataAccess.Recovery;

/// <summary>
/// No-op <see cref="IDatabaseRecoveryLogger"/> used when no logger is supplied.
/// </summary>
public sealed class NullDatabaseRecoveryLogger : IDatabaseRecoveryLogger
{
    /// <summary>Shared singleton instance.</summary>
    public static readonly NullDatabaseRecoveryLogger Instance = new();

    private NullDatabaseRecoveryLogger() { }

    /// <inheritdoc />
    public void Information(string message) { }

    /// <inheritdoc />
    public void Warning(string message) { }

    /// <inheritdoc />
    public void Error(Exception exception, string message) { }
}
