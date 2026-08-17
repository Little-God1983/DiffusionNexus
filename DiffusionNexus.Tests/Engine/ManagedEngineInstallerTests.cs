using DiffusionNexus.Installer.SDK.DataAccess;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Shared;
using DiffusionNexus.Installer.SDK.Shared.Services;
using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;
using Moq;
using SdkLogLevel = DiffusionNexus.Installer.SDK.Models.Enums.LogLevel;

namespace DiffusionNexus.Tests.Engine;

public class ManagedEngineInstallerTests
{
    /// <summary>
    /// Captures reports synchronously, unlike <see cref="Progress{T}"/> which marshals onto a
    /// captured <see cref="System.Threading.SynchronizationContext"/> (or the thread pool) and so
    /// cannot be asserted against deterministically in a unit test.
    /// </summary>
    private sealed class CapturingProgress<T> : IProgress<T>
    {
        public List<T> Reports { get; } = [];
        public void Report(T value) => Reports.Add(value);
    }

    private static InstallationConfiguration BuildKreaConfiguration()
    {
        var config = new InstallationConfiguration
        {
            Id = Guid.Parse(ManagedEngineInstaller.BaseConfigurationId),
            Name = "Krea-2-Turbo"
        };
        config.ModelDownloads.Add(new ModelDownload { Id = Guid.NewGuid(), Name = "Krea2 GGUF" });
        config.ModelDownloads.Add(new ModelDownload { Id = Guid.NewGuid(), Name = "qwen_image_vae" });
        config.GitRepositories.Add(new GitRepository { Id = Guid.NewGuid(), Name = "gguf" });
        config.Workflows.Add(new ComfUIWorkflow { Id = Guid.NewGuid(), Name = "1.Krea2-Turbo-Text2Image" });
        return config;
    }

    // PreInstallationResult.Context is `required` in the real SDK (not present in the brief's
    // sketch) — a minimal PreInstallationContext with TargetFolder/InstallationType satisfies it
    // without changing anything the tests assert on.
    private static PreInstallationResult CanProceedResult() => new()
    {
        Result = PreInstallationCheckResult.CanProceed,
        Context = new PreInstallationContext
        {
            TargetFolder = @"C:\Engine\ComfyUI",
            InstallationType = InstallationType.FullInstall
        }
    };

    private static (Mock<IInstallationCoordinator> Coordinator, Mock<IConfigurationRepository> Repo)
        Mocks(InstallationConfiguration config)
    {
        var repo = new Mock<IConfigurationRepository>();
        repo.Setup(r => r.GetByIdAsync(config.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var coordinator = new Mock<IInstallationCoordinator>();
        coordinator.Setup(c => c.EvaluateGpuGateAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<IUserPromptService>(),
                It.IsAny<Action<string, LogEntryLevel>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GpuGateOutcome.Proceed);
        coordinator.Setup(c => c.RunPreChecksAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationType>(),
                It.IsAny<IUserPromptService>(), It.IsAny<Action<string, LogEntryLevel>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CanProceedResult());
        coordinator.Setup(c => c.InstallAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationOptions>(),
                It.IsAny<IProgress<InstallLogEntry>>(), It.IsAny<IProgress<InstallationProgress>>(),
                It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<Func<CancellationToken>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(InstallationResult.Success("done", @"C:\Engine\ComfyUI", @"C:\Engine\ComfyUI\venv"));

        return (coordinator, repo);
    }

    private static ManagedEngineInstaller Create(
        Mock<IInstallationCoordinator> coordinator, Mock<IConfigurationRepository> repo)
        => new(coordinator.Object, repo.Object, new Mock<IUserPromptService>().Object);

    [Fact]
    public async Task InstallBaseEngine_ExcludesEveryPieceOfContent()
    {
        var config = BuildKreaConfiguration();
        var (coordinator, repo) = Mocks(config);
        InstallationOptions? captured = null;

        coordinator.Setup(c => c.InstallAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationOptions>(),
                It.IsAny<IProgress<InstallLogEntry>>(), It.IsAny<IProgress<InstallationProgress>>(),
                It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<Func<CancellationToken>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<InstallationConfiguration, string, InstallationOptions, IProgress<InstallLogEntry>,
                IProgress<InstallationProgress>, IProgress<DownloadProgress>, Func<CancellationToken>?,
                CancellationToken>((_, _, o, _, _, _, _, _) => captured = o)
            .ReturnsAsync(InstallationResult.Success("done", @"C:\Engine\ComfyUI"));

        var outcome = await Create(coordinator, repo).InstallBaseEngineAsync(
            new EngineInstallRequest(@"C:\Engine\ComfyUI", []),
            new Progress<InstallLogEntry>(), new Progress<InstallationProgress>(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.ExcludedModelIds.Should().BeEquivalentTo(config.ModelDownloads.Select(m => m.Id));
        captured.ExcludedNodeIds.Should().BeEquivalentTo(config.GitRepositories.Select(g => g.Id));
        captured.ExcludedWorkflowIds.Should().BeEquivalentTo(config.Workflows.Select(w => w.Id));
        captured.CpuTorch.Should().BeFalse("a GPU-gate Proceed means a usable NVIDIA GPU was found");
    }

    [Fact]
    public async Task InstallBaseEngine_NeverCreatesShortcuts()
    {
        var config = BuildKreaConfiguration();
        var (coordinator, repo) = Mocks(config);
        InstallationOptions? captured = null;

        coordinator.Setup(c => c.InstallAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationOptions>(),
                It.IsAny<IProgress<InstallLogEntry>>(), It.IsAny<IProgress<InstallationProgress>>(),
                It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<Func<CancellationToken>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<InstallationConfiguration, string, InstallationOptions, IProgress<InstallLogEntry>,
                IProgress<InstallationProgress>, IProgress<DownloadProgress>, Func<CancellationToken>?,
                CancellationToken>((_, _, o, _, _, _, _, _) => captured = o)
            .ReturnsAsync(InstallationResult.Success("done", @"C:\Engine\ComfyUI"));

        await Create(coordinator, repo).InstallBaseEngineAsync(
            new EngineInstallRequest(@"C:\Engine\ComfyUI", [@"D:\Models"]),
            new Progress<InstallLogEntry>(), new Progress<InstallationProgress>(), CancellationToken.None);

        captured!.CreateDesktopShortcut.Should().BeFalse();
        captured.CreateStartMenuShortcut.Should().BeFalse();
        captured.GenerateExtraModelPaths.Should().BeTrue(
            "the engine must read the shared model library instead of duplicating it");
    }

    [Fact]
    public async Task InstallBaseEngine_StopsWhenTheGpuGateBlocks()
    {
        var config = BuildKreaConfiguration();
        var (coordinator, repo) = Mocks(config);
        coordinator.Setup(c => c.EvaluateGpuGateAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<IUserPromptService>(),
                It.IsAny<Action<string, LogEntryLevel>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GpuGateOutcome.NoCompatibleGpu);

        var outcome = await Create(coordinator, repo).InstallBaseEngineAsync(
            new EngineInstallRequest(@"C:\Engine\ComfyUI", []),
            new Progress<InstallLogEntry>(), new Progress<InstallationProgress>(), CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse();
        outcome.Message.Should().Contain("GPU");
        coordinator.Verify(c => c.InstallAsync(
            It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationOptions>(),
            It.IsAny<IProgress<InstallLogEntry>>(), It.IsAny<IProgress<InstallationProgress>>(),
            It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<Func<CancellationToken>?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InstallBaseEngine_SetsCpuTorchWhenTheUserAcceptsTheCpuOnlyOffer()
    {
        var config = BuildKreaConfiguration();
        var (coordinator, repo) = Mocks(config);
        InstallationOptions? captured = null;

        coordinator.Setup(c => c.EvaluateGpuGateAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<IUserPromptService>(),
                It.IsAny<Action<string, LogEntryLevel>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GpuGateOutcome.ProceedCpuOnly);
        coordinator.Setup(c => c.InstallAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationOptions>(),
                It.IsAny<IProgress<InstallLogEntry>>(), It.IsAny<IProgress<InstallationProgress>>(),
                It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<Func<CancellationToken>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<InstallationConfiguration, string, InstallationOptions, IProgress<InstallLogEntry>,
                IProgress<InstallationProgress>, IProgress<DownloadProgress>, Func<CancellationToken>?,
                CancellationToken>((_, _, o, _, _, _, _, _) => captured = o)
            .ReturnsAsync(InstallationResult.Success("done", @"C:\Engine\ComfyUI"));

        var outcome = await Create(coordinator, repo).InstallBaseEngineAsync(
            new EngineInstallRequest(@"C:\Engine\ComfyUI", []),
            new Progress<InstallLogEntry>(), new Progress<InstallationProgress>(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue("accepting the CPU-only offer must still proceed, not block");
        captured.Should().NotBeNull();
        captured!.CpuTorch.Should().BeTrue(
            "the user accepted a CPU-only install; the SDK skips CUDA verification only when told");
        coordinator.Verify(c => c.InstallAsync(
            It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationOptions>(),
            It.IsAny<IProgress<InstallLogEntry>>(), It.IsAny<IProgress<InstallationProgress>>(),
            It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<Func<CancellationToken>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InstallBaseEngine_MapsEveryCoordinatorLogLevelToTheSdkLogEntry()
    {
        // Exercises the LogEntryLevel -> Models.Enums.LogLevel bridge in ManagedEngineInstaller's
        // LogAction: the two enums live in separate SDK assemblies with no shared source of
        // truth, so this pins the mapping runs and lands correctly instead of being dead code.
        var config = BuildKreaConfiguration();
        var (coordinator, repo) = Mocks(config);
        var logSink = new CapturingProgress<InstallLogEntry>();

        (LogEntryLevel Source, SdkLogLevel Expected)[] levels =
        [
            (LogEntryLevel.Debug, SdkLogLevel.Debug),
            (LogEntryLevel.Info, SdkLogLevel.Info),
            (LogEntryLevel.Success, SdkLogLevel.Success),
            (LogEntryLevel.Warning, SdkLogLevel.Warning),
            (LogEntryLevel.Error, SdkLogLevel.Error)
        ];

        coordinator.Setup(c => c.EvaluateGpuGateAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<IUserPromptService>(),
                It.IsAny<Action<string, LogEntryLevel>>(), It.IsAny<CancellationToken>()))
            .Callback<InstallationConfiguration, IUserPromptService, Action<string, LogEntryLevel>, CancellationToken>(
                (_, _, logAction, _) =>
                {
                    foreach (var (source, _) in levels)
                        logAction($"level {source}", source);
                })
            .ReturnsAsync(GpuGateOutcome.Proceed);

        var outcome = await Create(coordinator, repo).InstallBaseEngineAsync(
            new EngineInstallRequest(@"C:\Engine\ComfyUI", []),
            logSink, new Progress<InstallationProgress>(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        // Scoped to the "level " entries this test itself injects via the mocked coordinator
        // callback — InstallBaseEngineAsync also logs its own resolved-torch entry before the
        // gate even runs (see InstallBaseEngine_LogsTheResolvedTorchAndCudaVersions), which is
        // not part of what this test is pinning down.
        var levelReports = logSink.Reports.Where(e => e.Message.StartsWith("level ", StringComparison.Ordinal)).ToList();
        levelReports.Should().HaveCount(levels.Length);
        foreach (var (source, expected) in levels)
        {
            levelReports.Should().ContainSingle(e => e.Message == $"level {source}" && e.Level == expected,
                $"a {source} log entry must map to {nameof(SdkLogLevel)}.{expected}");
        }
    }

    [Fact]
    public async Task InstallBaseEngine_FailsClearlyWhenTheConfigurationIsMissing()
    {
        var repo = new Mock<IConfigurationRepository>();
        repo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InstallationConfiguration?)null);

        var outcome = await Create(new Mock<IInstallationCoordinator>(), repo).InstallBaseEngineAsync(
            new EngineInstallRequest(@"C:\Engine\ComfyUI", []),
            new Progress<InstallLogEntry>(), new Progress<InstallationProgress>(), CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse();
        outcome.Message.Should().Contain(ManagedEngineInstaller.BaseConfigurationId);
    }

    [Fact]
    public async Task InstallBaseEngine_LogsTheResolvedTorchAndCudaVersions()
    {
        // Review finding: the class doc asserted the engine "inherits" a specific CUDA/torch
        // pairing from the Krea-2-Turbo configuration, but that pairing is catalog data the
        // installer does not control and has already drifted from an earlier assumption once.
        // Rather than restate a pairing as guaranteed, the resolved configuration.Torch values
        // must be logged at install start so the truth is readable at runtime.
        var config = BuildKreaConfiguration();
        config.Torch = new TorchSettings { TorchVersion = "2.8.0", CudaVersion = "12.8" };
        var (coordinator, repo) = Mocks(config);
        var logSink = new CapturingProgress<InstallLogEntry>();

        var outcome = await Create(coordinator, repo).InstallBaseEngineAsync(
            new EngineInstallRequest(@"C:\Engine\ComfyUI", []),
            logSink, new Progress<InstallationProgress>(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        logSink.Reports.Should().Contain(e =>
                e.Message.Contains("2.8.0") && e.Message.Contains("12.8"),
            "the actually-resolved torch/CUDA pairing must be visible in the install log, not just assumed from a comment");
    }

    [Fact]
    public async Task InstallBaseEngine_ReportsCancellationDistinctlyFromFailure()
    {
        var config = BuildKreaConfiguration();
        var (coordinator, repo) = Mocks(config);
        coordinator.Setup(c => c.InstallAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationOptions>(),
                It.IsAny<IProgress<InstallLogEntry>>(), It.IsAny<IProgress<InstallationProgress>>(),
                It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<Func<CancellationToken>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(InstallationResult.Cancelled("user aborted"));

        var outcome = await Create(coordinator, repo).InstallBaseEngineAsync(
            new EngineInstallRequest(@"C:\Engine\ComfyUI", []),
            new Progress<InstallLogEntry>(), new Progress<InstallationProgress>(), CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse();
        outcome.IsCancelled.Should().BeTrue("a deliberate abort must not trigger failure reporting UI");
    }
}
