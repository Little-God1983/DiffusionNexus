using DiffusionNexus.Installer.SDK.DataAccess;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Shared;
using DiffusionNexus.Installer.SDK.Shared.Services;
using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Engine;

public class ManagedEngineInstallerTests
{
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
