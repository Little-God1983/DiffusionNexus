using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.UI.Services.Engine;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.InstallerManager;

/// <summary>
/// What the Engine tile shows while it installs (#609): reinstalling over a row whose folder is not a
/// working install used to keep the red "Installation folder not found" state for the whole install,
/// and every raw install-log line (pip's package lists included) replaced the step on the tile.
/// </summary>
public class EngineInstallTileTests
{
    private const string StalePath = @"C:\Old\Dead\Engine";

    private static InstallerPackage StaleRow() => new()
    {
        Id = 7,
        Name = "Diffusion Nexus Engine",
        InstallationPath = StalePath,
        ExecutablePath = null,
        Type = InstallerType.ComfyUI,
        IsAppManaged = true
    };

    private static (InstallerManagerViewModel Vm, Mock<IManagedEngineInstaller> Installer) Create(
        IReadOnlyList<InstallerPackage> packages, Task<EngineInstallOutcome> outcome)
    {
        var installer = new Mock<IManagedEngineInstaller>();
        installer.Setup(i => i.InstallBaseEngineAsync(
                It.IsAny<EngineInstallRequest>(), It.IsAny<IProgress<InstallLogEntry>>(),
                It.IsAny<IProgress<InstallationProgress>>(), It.IsAny<CancellationToken>()))
            .Returns(outcome);
        var vm = EngineTestHarness.CreateInstallerManagerViewModel(
            packages, engineInstaller: installer.Object, chosenFolder: @"C:\Engine\New", onPackageAdded: _ => { });
        vm.IsEngineTileVisible = true;
        return (vm, installer);
    }

    [Fact]
    public async Task StaleRow_WhileInstalling_HidesTheNotFoundBannerAndSaysInstalling()
    {
        var gate = new TaskCompletionSource<EngineInstallOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (vm, _) = Create([StaleRow()], gate.Task);
        await vm.LoadInstallationsCommand.ExecuteAsync(null);
        var card = vm.InstallerCards.Single(c => c.IsEngine);
        card.ShowMissingBanner.Should().BeTrue("before the install, the folder really is not a working install");

        var install = vm.InstallEngineAsync();

        card.ShowMissingBanner.Should().BeFalse("an install into it is running: the folder is on its way, not lost");
        card.VersionDisplay.Should().Be("Installing...");

        gate.SetResult(new EngineInstallOutcome(true, false, "done", @"C:\Engine\New"));
        await install;
    }

    [Fact]
    public async Task StaleRow_SuccessfulReinstall_LeavesNoNotFoundState()
    {
        var (vm, _) = Create([StaleRow()], Task.FromResult(new EngineInstallOutcome(true, false, "done", @"C:\Engine\New")));
        await vm.LoadInstallationsCommand.ExecuteAsync(null);
        var card = vm.InstallerCards.Single(c => c.IsEngine);

        await vm.InstallEngineAsync();

        card.IsMissing.Should().BeFalse("the reinstall put a working engine behind the row");
        card.ShowMissingBanner.Should().BeFalse();
        card.VersionDisplay.Should().Be("App-managed");
    }

    [Fact]
    public async Task StaleRow_FailedReinstall_ShowsNotFoundAgain()
    {
        var (vm, _) = Create([StaleRow()], Task.FromResult(new EngineInstallOutcome(false, false, "pip failed", null)));
        await vm.LoadInstallationsCommand.ExecuteAsync(null);
        var card = vm.InstallerCards.Single(c => c.IsEngine);

        await vm.InstallEngineAsync();

        card.ShowMissingBanner.Should().BeTrue("nothing was installed, so the row still points at a dead folder");
        card.VersionDisplay.Should().Be("Installation folder not found");
        card.ShowEngineInstallButton.Should().BeTrue();
    }

    [Fact]
    public async Task NoRow_FailedInstall_SaysNotInstalledAgain()
    {
        var (vm, _) = Create([], Task.FromResult(new EngineInstallOutcome(false, false, "pip failed", null)));
        await vm.LoadInstallationsCommand.ExecuteAsync(null);
        var card = vm.InstallerCards.Single(c => c.IsEngine);

        await vm.InstallEngineAsync();

        card.VersionDisplay.Should().Be("Not installed");
        card.ShowMissingBanner.Should().BeFalse();
    }

    [Fact]
    public async Task LogLines_GoToTheConsole_TheTileKeepsTheStep()
    {
        const string pipLine = "Installing collected packages: comfyui-frontend-package, comfyui-workflow-templates-core, " +
                               "comfyui-workflow-templates-json, torchsde, einops, click, hf-xet, idna, anyio, h11, truststore";
        var gate = new TaskCompletionSource<EngineInstallOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var installer = new Mock<IManagedEngineInstaller>();
        installer.Setup(i => i.InstallBaseEngineAsync(
                It.IsAny<EngineInstallRequest>(), It.IsAny<IProgress<InstallLogEntry>>(),
                It.IsAny<IProgress<InstallationProgress>>(), It.IsAny<CancellationToken>()))
            .Returns<EngineInstallRequest, IProgress<InstallLogEntry>, IProgress<InstallationProgress>, CancellationToken>(
                (_, log, steps, _) =>
                {
                    steps.Report(new InstallationProgress { CurrentStep = InstallationStep.PythonCheck, StepIndex = 4, TotalSteps = 10, Message = "Installing ComfyUI requirements" });
                    log.Report(new InstallLogEntry { Message = pipLine });
                    return gate.Task;
                });
        var vm = EngineTestHarness.CreateInstallerManagerViewModel(
            [], engineInstaller: installer.Object, chosenFolder: @"C:\Engine\New", onPackageAdded: _ => { });
        vm.IsEngineTileVisible = true;
        await vm.LoadInstallationsCommand.ExecuteAsync(null);
        var card = vm.InstallerCards.Single(c => c.IsEngine);

        // Progress<T> posts to the captured context: run its callbacks inline so the reports above
        // have landed by the time the install suspends on the gate.
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());
        Task install;
        try { install = vm.InstallEngineAsync(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }

        card.EngineStatusMessage.Should().Be("[5/10] Installing ComfyUI requirements",
            "raw log lines are for the Unified Console; the tile shows which step runs");
        card.EngineProgressPercent.Should().Be(40);

        gate.SetResult(new EngineInstallOutcome(true, false, "done", @"C:\Engine\New"));
        await install;
    }

    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }
}
