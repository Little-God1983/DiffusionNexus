using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Shared;

namespace DiffusionNexus.UI.Services.Engine;

/// <summary>What the caller wants installed and where.</summary>
/// <param name="InstallRoot">Target folder chosen by the user.</param>
/// <param name="SharedModelRoots">
/// Existing model libraries the engine should read through extra_model_paths.yaml, so workload
/// models are never duplicated.
/// </param>
public sealed record EngineInstallRequest(string InstallRoot, IReadOnlyList<string> SharedModelRoots);

/// <summary>Result of a base-engine install.</summary>
public sealed record EngineInstallOutcome(
    bool IsSuccess,
    bool IsCancelled,
    string Message,
    string? RepositoryPath);

/// <summary>
/// Installs the base engine: ComfyUI + venv + torch, with no models, custom nodes or workflows.
/// Workload content is added afterwards through the ordinary workload installer.
/// </summary>
public interface IManagedEngineInstaller
{
    Task<EngineInstallOutcome> InstallBaseEngineAsync(
        EngineInstallRequest request,
        IProgress<InstallLogEntry> logProgress,
        IProgress<InstallationProgress> stepProgress,
        CancellationToken cancellationToken);
}
