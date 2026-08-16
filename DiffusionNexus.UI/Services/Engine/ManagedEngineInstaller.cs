using DiffusionNexus.Installer.SDK.DataAccess;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Shared;
using DiffusionNexus.Installer.SDK.Shared.Services;
using Serilog;

namespace DiffusionNexus.UI.Services.Engine;

/// <summary>
/// Drives the SDK's installation coordinator to produce a bare, app-owned ComfyUI.
///
/// The base engine is built from the Krea-2-Turbo configuration with every model, custom node
/// and workflow excluded. That configuration is what pins the engine to CUDA 13.0 + torch
/// 2.11.0 — the pairing the project standardized on — so no torch settings are authored here.
/// Content arrives afterwards through the app's ordinary workload installer.
/// </summary>
public sealed class ManagedEngineInstaller : IManagedEngineInstaller
{
    private static readonly ILogger Logger = Log.ForContext<ManagedEngineInstaller>();

    /// <summary>Catalog id of the configuration that defines the engine base (Krea-2-Turbo).</summary>
    public const string BaseConfigurationId = "E79C079A-2FD7-4FE7-8086-23731092555D";

    private readonly IInstallationCoordinator _coordinator;
    private readonly IConfigurationRepository _configurationRepository;
    private readonly IUserPromptService _promptService;

    public ManagedEngineInstaller(
        IInstallationCoordinator coordinator,
        IConfigurationRepository configurationRepository,
        IUserPromptService promptService)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(configurationRepository);
        ArgumentNullException.ThrowIfNull(promptService);

        _coordinator = coordinator;
        _configurationRepository = configurationRepository;
        _promptService = promptService;
    }

    public async Task<EngineInstallOutcome> InstallBaseEngineAsync(
        EngineInstallRequest request,
        IProgress<InstallLogEntry> logProgress,
        IProgress<InstallationProgress> stepProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(logProgress);
        ArgumentNullException.ThrowIfNull(stepProgress);

        void LogAction(string message, LogEntryLevel level)
        {
            Logger.Information("Engine install: {Message}", message);
            // InstallLogEntry.Level is Models.Enums.LogLevel, distinct from the Shared.LogEntryLevel
            // the coordinator's logAction callback uses. Both enums share the same member names
            // for every level the callback can report, so a name-based parse is a safe bridge.
            logProgress.Report(new InstallLogEntry
            {
                Message = message,
                Level = Enum.Parse<DiffusionNexus.Installer.SDK.Models.Enums.LogLevel>(level.ToString())
            });
        }

        var configuration = await _configurationRepository
            .GetByIdAsync(Guid.Parse(BaseConfigurationId), cancellationToken)
            .ConfigureAwait(false);

        if (configuration is null)
        {
            return new EngineInstallOutcome(false, false,
                $"The engine base configuration {BaseConfigurationId} was not found in the catalog database. " +
                "The shipped catalog may be out of date.", null);
        }

        var gate = await _coordinator
            .EvaluateGpuGateAsync(configuration, _promptService, LogAction, cancellationToken)
            .ConfigureAwait(false);

        if (gate is GpuGateOutcome.NoCompatibleGpu or GpuGateOutcome.Cancelled)
        {
            return new EngineInstallOutcome(false, gate == GpuGateOutcome.Cancelled,
                $"GPU pre-flight stopped the engine install ({gate}). The engine needs a usable NVIDIA GPU.",
                null);
        }

        var preChecks = await _coordinator
            .RunPreChecksAsync(configuration, request.InstallRoot, InstallationType.FullInstall,
                _promptService, LogAction, cancellationToken)
            .ConfigureAwait(false);

        if (preChecks.Result != PreInstallationCheckResult.CanProceed)
        {
            return new EngineInstallOutcome(false, false,
                $"Pre-installation checks did not pass: {preChecks.Result}.", null);
        }

        var options = BuildBaseOnlyOptions(configuration, request);

        var result = await _coordinator.InstallAsync(
                configuration, request.InstallRoot, options,
                logProgress, stepProgress, new Progress<DownloadProgress>(),
                skipDownloadTokenProvider: null, cancellationToken)
            .ConfigureAwait(false);

        return new EngineInstallOutcome(
            result.IsSuccess, result.IsCancelled, result.Message, result.RepositoryPath);
    }

    /// <summary>
    /// Builds options that install the environment only: every declared model, custom node and
    /// workflow is excluded, shortcuts are off (the engine is not a user-launchable app), and
    /// extra_model_paths.yaml is generated so the engine reads the shared model library.
    /// </summary>
    private static InstallationOptions BuildBaseOnlyOptions(
        DiffusionNexus.Installer.SDK.Models.Configuration.InstallationConfiguration configuration,
        EngineInstallRequest request)
    {
        return InstallationOptions.Default with
        {
            ExcludedModelIds = [.. configuration.ModelDownloads.Select(m => m.Id)],
            ExcludedNodeIds = [.. configuration.GitRepositories.Select(g => g.Id)],
            ExcludedWorkflowIds = [.. configuration.Workflows.Select(w => w.Id)],
            CreateDesktopShortcut = false,
            CreateStartMenuShortcut = false,
            GenerateExtraModelPaths = true,
            OverwriteExtraModelPaths = true,
            ModelBaseFolder = request.SharedModelRoots.FirstOrDefault(),
            VerboseLogging = true
        };
    }
}
