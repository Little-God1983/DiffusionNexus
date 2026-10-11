namespace DiffusionNexus.UI.Services.Engine;

/// <summary>
/// The Engine as the Unified Console's instance row drives it (#610): the user's own Start, Stop and
/// Restart. Kept apart from <see cref="IManagedComfyUiEngine"/>, which is for callers that must never
/// stop the engine (a stop there could kill another consumer's job); here a person pressed Stop, and
/// <see cref="EngineConsoleControl"/> asks first when a generation is running.
/// </summary>
public interface IManagedEngineControl
{
    /// <summary>True while the engine process runs and answered its readiness check.</summary>
    bool IsRunning { get; }

    /// <summary>Base URL of the running engine, or null when it is not running.</summary>
    string? BaseUrl { get; }

    /// <summary>True while a job (a generation) runs on the engine.</summary>
    bool HasActiveJobs { get; }

    /// <summary>
    /// Raised when the engine starts (ready) or stops, whoever caused it: the console, the Canvas
    /// starting it on demand, the app's shutdown, or the process exiting on its own. Once per change.
    /// May be raised on any thread.
    /// </summary>
    event Action<bool>? RunningStateChanged;

    /// <summary>Starts the engine if needed. Never throws for ordinary failures; see <see cref="EngineStartResult"/>.</summary>
    Task<EngineStartResult> EnsureRunningAsync(string installRoot, CancellationToken ct);

    /// <summary>Stops the engine, a running job included. Safe to call when it is not running.</summary>
    Task StopAsync();
}
