namespace DiffusionNexus.DataAccess.Recovery;

/// <summary>
/// Minimal logging sink used by <see cref="DatabaseRecoveryService"/>.
/// <para>
/// Kept intentionally tiny so the DataAccess project stays free of any concrete logging
/// dependency (Serilog, Microsoft.Extensions.Logging, …). The UI adapts its Serilog sink to
/// this interface; unit tests pass a capturing or the shared <see cref="NullDatabaseRecoveryLogger"/>
/// instance.
/// </para>
/// <para>
/// The three shapes below cover every call the recovery code makes: informational text,
/// a warning (message only), and an error carrying an exception. Structured message templates
/// from the original App.axaml.cs implementation are rendered to plain strings before being
/// passed here — the rendered text is preserved, only the named Serilog properties are lost.
/// </para>
/// </summary>
public interface IDatabaseRecoveryLogger
{
    /// <summary>Logs an informational message.</summary>
    void Information(string message);

    /// <summary>Logs a warning message (no exception).</summary>
    void Warning(string message);

    /// <summary>Logs an error message with the associated exception.</summary>
    void Error(Exception exception, string message);
}
