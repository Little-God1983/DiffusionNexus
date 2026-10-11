namespace DiffusionNexus.UI.Services.Engine;

/// <summary>
/// The Engine as the Unified Console's instance row drives it (#610): the user's own Start, Stop and
/// Restart. Adds stopping and state to <see cref="IManagedComfyUiEngine"/>, which stays the interface
/// for callers that must never stop the engine (a stop there could kill another consumer's job); here
/// a person pressed Stop, and <see cref="EngineConsoleControl"/> asks first when a generation is running.
/// </summary>
public interface IManagedEngineControl : IManagedComfyUiEngine
{
    /// <summary>True while the engine process runs and answered its readiness check.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// True while a start runs (spawn and readiness wait, up to two minutes), whoever started it: the
    /// console, the Canvas on demand, or a pending restart carried out.
    /// </summary>
    bool IsStarting { get; }

    /// <summary>True while a generation runs on the engine or is getting under way (<see cref="IManagedComfyUiEngine.BeginPreparing"/>).</summary>
    bool HasActiveJobs { get; }

    /// <summary>
    /// Raised when <see cref="IsRunning"/> or <see cref="IsStarting"/> changed, whoever caused it: the
    /// console, the Canvas, the app's shutdown, or the process exiting on its own. On any thread; reads
    /// of the two properties give the state as of then.
    /// </summary>
    event Action? StateChanged;

    /// <summary>Stops the engine, a running job included, or aborts a start. Safe to call when it is not running.</summary>
    Task StopAsync();

    /// <summary>
    /// Stops the engine unless a generation runs on it or is getting under way, decided under the
    /// start lock so a generation that arrives meanwhile either counts or waits and starts it afresh.
    /// False when it was busy and left running.
    /// </summary>
    Task<bool> StopIfIdleAsync();
}
