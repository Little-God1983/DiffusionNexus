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

    /// <summary>
    /// Marks a generation getting under way (resolving, starting, probing) until its job begins.
    /// Counts for the console's Stop, which asks first, but not for a pending restart: the
    /// generation that carries the restart out must not postpone it. Disposing more than once is
    /// harmless. The default marks nothing.
    /// </summary>
    IDisposable BeginPreparing() => NoOp.Instance;

    /// <summary>
    /// Holds the engine for maintenance (an update changing its folder) until disposed: no start runs
    /// meanwhile, whoever asks, and <see cref="EnsureRunningAsync"/> says why. The default holds nothing.
    /// </summary>
    IDisposable BeginMaintenance() => NoOp.Instance;

    private sealed class NoOp : IDisposable
    {
        public static readonly NoOp Instance = new();
        public void Dispose() { }
    }
}
