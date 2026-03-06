using DiffusionNexus.Domain.Services.UnifiedLogging;

namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Decouples instance lifecycle management from any specific view.
/// Any component can start/stop instances via DI.
/// Process stdout/stderr is automatically piped into <see cref="IUnifiedLogger"/>
/// with <see cref="LogCategory.InstanceManagement"/>.
/// Registered as a singleton in DI.
/// </summary>
public interface IInstanceProcessManager
{
    /// <summary>
    /// Launches an instance and returns a tracked task handle for the run.
    /// </summary>
    /// <param name="instanceId">Unique identifier (typically the InstallerPackage Id as string).</param>
    /// <returns>A handle whose lifetime spans the process run.</returns>
    Task<ITrackedTaskHandle> StartInstanceAsync(string instanceId);

    /// <summary>
    /// Gracefully stops a running instance (stdin close → wait → force kill).
    /// </summary>
    Task StopInstanceAsync(string instanceId);

    /// <summary>
    /// Forcefully kills a running instance immediately.
    /// </summary>
    Task KillInstanceAsync(string instanceId);

    /// <summary>
    /// Observable stream of currently running instance IDs.
    /// Emits a new snapshot whenever an instance starts or stops.
    /// </summary>
    IObservable<IReadOnlySet<string>> RunningInstances { get; }

    /// <summary>
    /// Checks whether a specific instance is currently running.
    /// </summary>
    bool IsRunning(string instanceId);

    /// <summary>
    /// Gets the detected web URL for a running instance, if any.
    /// </summary>
    string? GetDetectedUrl(string instanceId);
}
