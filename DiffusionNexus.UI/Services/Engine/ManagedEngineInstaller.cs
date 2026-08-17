using DiffusionNexus.Installer.SDK.DataAccess;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Shared;
using DiffusionNexus.Installer.SDK.Shared.Services;
using Serilog;
using SdkLogLevel = DiffusionNexus.Installer.SDK.Models.Enums.LogLevel;

namespace DiffusionNexus.UI.Services.Engine;

/// <summary>
/// Drives the SDK's installation coordinator to produce a bare, app-owned ComfyUI.
///
/// The base engine is built from the Krea-2-Turbo configuration with every model, custom node
/// and workflow excluded. No torch settings are authored here — the engine simply inherits
/// whatever <c>configuration.Torch</c> declares for that catalog entry, whatever it happens to be
/// pinned to at install time. Do not restate a specific CUDA/torch pairing here as guaranteed: it
/// is catalog data, not something this class controls, and it has already drifted from an earlier
/// assumption once (the catalog currently declares CUDA 12.8 + torch 2.8.0). The actual resolved
/// values are logged at install start below so the truth is readable at runtime instead of
/// inferred from a comment that can go stale again.
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
            logProgress.Report(new InstallLogEntry
            {
                Message = message,
                Level = ToSdkLogLevel(level)
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

        // The engine authors no torch settings of its own (see this class's doc comment) — log
        // what the base configuration actually resolved to so it's readable at runtime instead of
        // assumed from a comment that can go stale. Empty TorchVersion means "latest" (see
        // TorchSettings.TorchVersion's own doc), so name that explicitly rather than logging a
        // blank value that reads like a bug.
        var torchVersionDisplay = string.IsNullOrWhiteSpace(configuration.Torch.TorchVersion)
            ? "latest"
            : configuration.Torch.TorchVersion;
        LogAction(
            $"Base engine configuration '{configuration.Name}' resolved torch {torchVersionDisplay} " +
            $"+ CUDA {configuration.Torch.CudaVersion}.",
            LogEntryLevel.Info);

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

        var options = BuildBaseOnlyOptions(configuration, request, gate);

        var result = await _coordinator.InstallAsync(
                configuration, request.InstallRoot, options,
                logProgress, stepProgress, new Progress<DownloadProgress>(),
                skipDownloadTokenProvider: null, cancellationToken)
            .ConfigureAwait(false);

        return new EngineInstallOutcome(
            result.IsSuccess, result.IsCancelled, result.Message, result.RepositoryPath);
    }

    /// <summary>
    /// Maps the coordinator's <see cref="LogEntryLevel"/> (used by its <c>logAction</c> callback)
    /// onto <see cref="InstallLogEntry.Level"/>'s <c>Models.Enums.LogLevel</c>. The two enums live
    /// in separate SDK assemblies with no shared source of truth and are maintained independently
    /// — an <c>Enum.Parse</c> name bridge would compile today and throw a <see cref="FormatException"/>
    /// from inside the coordinator's own log callback, mid-install, the moment either enum's member
    /// names drift. An explicit, total switch instead fails to compile on drift and can never throw.
    /// </summary>
    private static SdkLogLevel ToSdkLogLevel(LogEntryLevel level) => level switch
    {
        LogEntryLevel.Debug => SdkLogLevel.Debug,
        LogEntryLevel.Info => SdkLogLevel.Info,
        LogEntryLevel.Success => SdkLogLevel.Success,
        LogEntryLevel.Warning => SdkLogLevel.Warning,
        LogEntryLevel.Error => SdkLogLevel.Error,
        _ => SdkLogLevel.Info
    };

    /// <summary>
    /// Builds options that install the environment only: every declared model, custom node and
    /// workflow is excluded, shortcuts are off (the engine is not a user-launchable app), and
    /// extra_model_paths.yaml is generated so the engine reads the shared model library.
    ///
    /// <para>
    /// The SDK's generator is <em>not</em> the authority on that file. Its
    /// <see cref="InstallationOptions.ModelBaseFolder"/> holds a single path, so it can only ever
    /// declare the first registered library — which left every other one invisible to the engine.
    /// <see cref="EngineModelPathsSynchronizer"/> rewrites the file with all of them once the
    /// install lands, and again before each engine start. Generation stays enabled here so a
    /// failure in that rewrite still leaves a working single-library file behind rather than none.
    /// </para>
    /// </summary>
    /// <param name="gate">
    /// The GPU gate's outcome. <see cref="InstallationOptions.CpuTorch"/> is documented as
    /// caller-set: "installs the CPU-only PyTorch build and skips CUDA verification... Set when
    /// the user accepted the CPU-only fallback." The gate is what learns whether the user
    /// accepted that offer, so it — not the configuration — is the only place this flag can
    /// correctly come from. Without this, a no-GPU user who accepts the CPU-only offer would get
    /// the CUDA torch wheel from the Krea-2-Turbo configuration and fail CUDA verification later,
    /// well past the point where they answered the question meant to prevent exactly that.
    /// </param>
    private static InstallationOptions BuildBaseOnlyOptions(
        DiffusionNexus.Installer.SDK.Models.Configuration.InstallationConfiguration configuration,
        EngineInstallRequest request,
        GpuGateOutcome gate)
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
            VerboseLogging = true,
            CpuTorch = gate == GpuGateOutcome.ProceedCpuOnly
        };
    }
}
