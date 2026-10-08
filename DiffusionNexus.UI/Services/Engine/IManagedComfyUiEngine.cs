namespace DiffusionNexus.UI.Services.Engine;

/// <summary>
/// The part of <see cref="ManagedComfyUiEngine"/> that callers outside the Canvas need: start it on
/// demand and learn its loopback URL. Exists so the ComfyUI client provider can be unit-tested
/// without spawning Python.
/// </summary>
public interface IManagedComfyUiEngine
{
    /// <summary>Base URL of the running engine, or null when it is not running.</summary>
    string? BaseUrl { get; }

    /// <summary>Starts the engine if needed. Never throws for ordinary failures; see <see cref="EngineStartResult"/>.</summary>
    Task<EngineStartResult> EnsureRunningAsync(string installRoot, CancellationToken ct);

    /// <summary>Stops the engine if it is running; the next <see cref="EnsureRunningAsync"/> starts it fresh.</summary>
    Task StopAsync();
}
