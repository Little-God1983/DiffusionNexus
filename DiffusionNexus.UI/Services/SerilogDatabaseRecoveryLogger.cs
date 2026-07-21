using DiffusionNexus.DataAccess.Recovery;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Adapts the DataAccess <see cref="IDatabaseRecoveryLogger"/> to the application's static Serilog
/// sink, so <see cref="DatabaseRecoveryService"/> logs land in the same file/console as the rest of
/// startup. Keeps the DataAccess project free of any Serilog dependency.
/// </summary>
internal sealed class SerilogDatabaseRecoveryLogger : IDatabaseRecoveryLogger
{
    /// <inheritdoc />
    public void Information(string message) => Serilog.Log.Information("{Message}", message);

    /// <inheritdoc />
    public void Warning(string message) => Serilog.Log.Warning("{Message}", message);

    /// <inheritdoc />
    public void Error(Exception exception, string message) => Serilog.Log.Error(exception, "{Message}", message);
}
