namespace DiffusionNexus.UI.Services.Engine;

/// <summary>
/// The part of <see cref="ManagedComfyUiEngine"/> that callers outside the Canvas need: start it on
/// demand, learn its loopback URL and ask for a restart. Exists so the ComfyUI client provider can be
/// unit-tested without spawning Python. Stopping is left to the app's shutdown, on the concrete
/// class: a caller here must never kill a job running on the engine.
/// </summary>
public interface IManagedComfyUiEngine
{
    /// <summary>Base URL of the running engine, or null when it is not running.</summary>
    string? BaseUrl { get; }

    /// <summary>Starts the engine if needed. Never throws for ordinary failures; see <see cref="EngineStartResult"/>.</summary>
    Task<EngineStartResult> EnsureRunningAsync(string installRoot, CancellationToken ct);

    /// <summary>
    /// Asks for a fresh process on the next <see cref="EnsureRunningAsync"/>, e.g. so ComfyUI loads
    /// newly installed node packs. Never stops the engine now, so a job running on it is not killed.
    /// </summary>
    void RequestRestart();

    /// <summary>True while a <see cref="RequestRestart"/> has not been carried out yet.</summary>
    bool IsRestartPending { get; }

    /// <summary>
    /// Marks a job as running on the engine until the returned token is disposed. A pending restart
    /// waits until no job is running. Disposing the token more than once is harmless.
    /// </summary>
    IDisposable BeginJob();
}
