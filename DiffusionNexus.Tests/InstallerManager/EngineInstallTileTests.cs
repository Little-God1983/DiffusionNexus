using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Engine;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;
using SdkLogLevel = DiffusionNexus.Installer.SDK.Models.Enums.LogLevel;

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

    [Fact]
    public async Task FailedInstall_TheTileShowsTheFailureBeforeTheDialogOpens()
    {
        InstallerPackageCardViewModel? card = null;
        (string? Version, bool Installing, bool Banner) atDialog = default;
        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowOpenFolderDialogAsync(It.IsAny<string>())).ReturnsAsync(@"C:\Engine\New");
        dialog.Setup(d => d.ShowConfirmAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Callback(() => atDialog = (card!.VersionDisplay, card.IsEngineInstalling, card.ShowMissingBanner))
            .ReturnsAsync(false);
        var vm = EngineTestHarness.CreateInstallerManagerViewModel(
            [StaleRow()], engineInstaller: Installer(Task.FromResult(new EngineInstallOutcome(false, false, "pip failed", null))),
            onPackageAdded: _ => { }, dialogMock: dialog);
        vm.IsEngineTileVisible = true;
        await vm.LoadInstallationsCommand.ExecuteAsync(null);
        card = vm.InstallerCards.Single(c => c.IsEngine);

        await vm.InstallEngineAsync();

        atDialog.Installing.Should().BeFalse("the install is over when its failure is reported");
        atDialog.Version.Should().Be("Installation folder not found");
        atDialog.Banner.Should().BeTrue();
    }

    [Fact]
    public async Task InstallThatThrows_TheTileShowsTheFailureBeforeTheMessageOpens()
    {
        InstallerPackageCardViewModel? card = null;
        bool? installingAtMessage = null;
        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowOpenFolderDialogAsync(It.IsAny<string>())).ReturnsAsync(@"C:\Engine\New");
        dialog.Setup(d => d.ShowMessageAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Callback(() => installingAtMessage = card!.IsEngineInstalling)
            .Returns(Task.CompletedTask);
        var vm = EngineTestHarness.CreateInstallerManagerViewModel(
            [StaleRow()], engineInstaller: Installer(Task.FromException<EngineInstallOutcome>(new IOException("disk full"))),
            onPackageAdded: _ => { }, dialogMock: dialog);
        vm.IsEngineTileVisible = true;
        await vm.LoadInstallationsCommand.ExecuteAsync(null);
        card = vm.InstallerCards.Single(c => c.IsEngine);

        await vm.InstallEngineAsync();

        installingAtMessage.Should().BeFalse();
        card.VersionDisplay.Should().Be("Installation folder not found");
    }

    [Fact]
    public async Task InstalledEngine_FailedInstall_KeepsAppManaged()
    {
        var root = EngineFolder(withMainPy: true);
        try
        {
            var row = StaleRow();
            row.InstallationPath = root;
            var (vm, _) = Create([row], Task.FromResult(new EngineInstallOutcome(false, false, "pip failed", null)));
            await vm.LoadInstallationsCommand.ExecuteAsync(null);
            var card = vm.InstallerCards.Single(c => c.IsEngine);
            card.VersionDisplay.Should().Be("App-managed");

            await vm.InstallEngineAsync();

            card.VersionDisplay.Should().Be("App-managed", "a failed install must not leave the tile on \"Installing...\"");
            card.InstallationPath.Should().Be(root);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Reinstall_ShowsThePickedFolderWhileInstalling_ButCardActionsKeepTheRowsFolder()
    {
        var gate = new TaskCompletionSource<EngineInstallOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (vm, _) = Create([StaleRow()], gate.Task);
        await vm.LoadInstallationsCommand.ExecuteAsync(null);
        var card = vm.InstallerCards.Single(c => c.IsEngine);

        var install = vm.InstallEngineAsync();
        card.PathDisplay.Should().Be(@"C:\Engine\New", "that is where the install goes");
        card.InstallationPath.Should().Be(StalePath,
            "Delete from Disk, Settings and Open Folder act on InstallationPath: never on the folder being installed into");

        gate.SetResult(new EngineInstallOutcome(false, false, "pip failed", null));
        await install;
        card.PathDisplay.Should().Be(StalePath, "nothing was installed: the row still points there");
        card.InstallationPath.Should().Be(StalePath);
    }

    [Fact]
    public async Task FollowUpThatThrows_AfterASuccessfulInstall_IsNotReportedAsAFailedInstall()
    {
        var aggregator = new Mock<IDatasetEventAggregator>();
        aggregator.Setup(a => a.PublishInstallerPackagesChanged(It.IsAny<InstallerPackagesChangedEventArgs>()))
            .Throws(new InvalidOperationException("subscriber failed"));
        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowOpenFolderDialogAsync(It.IsAny<string>())).ReturnsAsync(@"C:\Engine\New");
        var vm = EngineTestHarness.CreateInstallerManagerViewModel(
            [], engineInstaller: Installer(Task.FromResult(new EngineInstallOutcome(true, false, "done", @"C:\Engine\New"))),
            onPackageAdded: _ => { }, dialogMock: dialog, eventAggregatorMock: aggregator);
        vm.IsEngineTileVisible = true;
        await vm.LoadInstallationsCommand.ExecuteAsync(null);
        var card = vm.InstallerCards.Single(c => c.IsEngine);

        await vm.InstallEngineAsync();

        card.IsEngineInstalled.Should().BeTrue();
        dialog.Verify(d => d.ShowMessageAsync("Engine install failed", It.IsAny<string>()), Times.Never,
            "the engine is installed and its row saved; a follow-up step failing does not undo that");
        aggregator.Verify(a => a.PublishEngineChanged(It.IsAny<EngineChangedEventArgs>()), Times.Once,
            "one follow-up failing must not skip the others");
    }

    [Fact]
    public async Task FailureDialogThatThrows_DoesNotEscapeTheInstall()
    {
        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowOpenFolderDialogAsync(It.IsAny<string>())).ReturnsAsync(@"C:\Engine\New");
        dialog.Setup(d => d.ShowConfirmAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("no owner window"));
        var vm = EngineTestHarness.CreateInstallerManagerViewModel(
            [StaleRow()], engineInstaller: Installer(Task.FromResult(new EngineInstallOutcome(false, false, "pip failed", null))),
            onPackageAdded: _ => { }, dialogMock: dialog);
        vm.IsEngineTileVisible = true;
        await vm.LoadInstallationsCommand.ExecuteAsync(null);

        var act = () => vm.InstallEngineAsync();

        await act.Should().NotThrowAsync("the install is over; a dialog that cannot open is logged, not a crash");
    }

    [Fact]
    public async Task LateProgressReports_AfterTheInstallEnded_LeaveTheTileClean()
    {
        IProgress<InstallLogEntry>? log = null;
        IProgress<InstallationProgress>? steps = null;
        var installer = new Mock<IManagedEngineInstaller>();
        installer.Setup(i => i.InstallBaseEngineAsync(
                It.IsAny<EngineInstallRequest>(), It.IsAny<IProgress<InstallLogEntry>>(),
                It.IsAny<IProgress<InstallationProgress>>(), It.IsAny<CancellationToken>()))
            .Returns<EngineInstallRequest, IProgress<InstallLogEntry>, IProgress<InstallationProgress>, CancellationToken>(
                (_, l, p, _) => { log = l; steps = p; return Task.FromResult(new EngineInstallOutcome(false, false, "pip failed", null)); });
        var vm = EngineTestHarness.CreateInstallerManagerViewModel(
            [], engineInstaller: installer.Object, chosenFolder: @"C:\Engine\New", onPackageAdded: _ => { });
        vm.IsEngineTileVisible = true;
        await vm.LoadInstallationsCommand.ExecuteAsync(null);
        var card = vm.InstallerCards.Single(c => c.IsEngine);

        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());
        try
        {
            await vm.InstallEngineAsync();
            // Progress<T> posts: a report queued as the SDK returned lands after the install ended.
            steps!.Report(new InstallationProgress { CurrentStep = InstallationStep.PythonCheck, StepIndex = 9, TotalSteps = 10, Message = "Verifying" });
            log!.Report(new InstallLogEntry { Message = "late line" });
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }

        card.EngineStatusMessage.Should().BeNull();
        card.EngineActivity.Should().BeNull();
        card.EngineProgressPercent.Should().Be(0, "0 when idle");
    }

    [Fact]
    public void FolderThatExistsWithoutAnEngine_SaysIncomplete_NotNotFound()
    {
        var root = EngineFolder(withMainPy: false);
        try
        {
            var row = StaleRow();
            row.InstallationPath = root;

            var card = InstallerPackageCardViewModel.CreateEngineCard(row);

            card.ShowMissingBanner.Should().BeTrue();
            card.MissingBannerText.Should().Be("Installation incomplete");
            card.VersionDisplay.Should().Be("Installation incomplete");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Probe_TellsAMissingAnIncompleteAndAnInstalledFolderApart()
    {
        var empty = EngineFolder(withMainPy: false);
        var installed = EngineFolder(withMainPy: true);
        try
        {
            ManagedEngineLocator.Probe(StalePath).Should().Be(EngineFolderState.Missing);
            ManagedEngineLocator.Probe("").Should().Be(EngineFolderState.Missing);
            ManagedEngineLocator.Probe(empty).Should().Be(EngineFolderState.Incomplete);
            ManagedEngineLocator.Probe(installed).Should().Be(EngineFolderState.Installed);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
            Directory.Delete(installed, recursive: true);
        }
    }

    [Fact]
    public void MissingFolder_SaysNotFound()
    {
        var card = InstallerPackageCardViewModel.CreateEngineCard(StaleRow());

        card.MissingBannerText.Should().Be("Installation folder not found");
        card.VersionDisplay.Should().Be("Installation folder not found");
    }

    [Fact]
    public async Task LogEntries_KeepTheirLevelInTheConsole_AndTheLatestShowsAsOneActivityLine()
    {
        var logger = new Mock<IUnifiedLogger>();
        var gate = new TaskCompletionSource<EngineInstallOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var installer = new Mock<IManagedEngineInstaller>();
        installer.Setup(i => i.InstallBaseEngineAsync(
                It.IsAny<EngineInstallRequest>(), It.IsAny<IProgress<InstallLogEntry>>(),
                It.IsAny<IProgress<InstallationProgress>>(), It.IsAny<CancellationToken>()))
            .Returns<EngineInstallRequest, IProgress<InstallLogEntry>, IProgress<InstallationProgress>, CancellationToken>(
                (_, log, _, _) =>
                {
                    log.Report(new InstallLogEntry { Message = "pip install failed", Level = SdkLogLevel.Error });
                    log.Report(new InstallLogEntry { Message = "no GPU found", Level = SdkLogLevel.Warning });
                    log.Report(new InstallLogEntry { Message = "Collecting torch\nDownloading torch-2.8.0.whl", Level = SdkLogLevel.Info });
                    log.Report(new InstallLogEntry { Message = "Executing: pip install torch", Level = SdkLogLevel.Debug });
                    log.Report(new InstallLogEntry { Message = "python crashed", Level = SdkLogLevel.Critical });
                    log.Report(new InstallLogEntry { Message = "verbose detail", Level = SdkLogLevel.Trace });
                    return gate.Task;
                });
        var vm = EngineTestHarness.CreateInstallerManagerViewModel(
            [], engineInstaller: installer.Object, chosenFolder: @"C:\Engine\New", onPackageAdded: _ => { }, loggerMock: logger);
        vm.IsEngineTileVisible = true;
        await vm.LoadInstallationsCommand.ExecuteAsync(null);
        var card = vm.InstallerCards.Single(c => c.IsEngine);

        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());
        Task install;
        try { install = vm.InstallEngineAsync(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }

        logger.Verify(l => l.Error(LogCategory.Installation, "Diffusion Nexus Engine", "pip install failed", null), Times.Once);
        logger.Verify(l => l.Warn(LogCategory.Installation, "Diffusion Nexus Engine", "no GPU found", null), Times.Once);
        logger.Verify(l => l.Debug(LogCategory.Installation, "Diffusion Nexus Engine", "Executing: pip install torch", null), Times.Once);
        logger.Verify(l => l.Trace(LogCategory.Installation, "Diffusion Nexus Engine", "verbose detail", null), Times.Once);
        logger.Verify(l => l.Error(LogCategory.Installation, "Diffusion Nexus Engine", "python crashed", null), Times.Once);
        card.EngineActivity.Should().Be("python crashed",
            "the tile shows the latest line that is not a command or verbose detail (Debug, Trace stay in the console)");

        gate.SetResult(new EngineInstallOutcome(true, false, "done", @"C:\Engine\New"));
        await install;
        card.EngineActivity.Should().BeNull();
    }

    private static IManagedEngineInstaller Installer(Task<EngineInstallOutcome> outcome)
    {
        var installer = new Mock<IManagedEngineInstaller>();
        installer.Setup(i => i.InstallBaseEngineAsync(
                It.IsAny<EngineInstallRequest>(), It.IsAny<IProgress<InstallLogEntry>>(),
                It.IsAny<IProgress<InstallationProgress>>(), It.IsAny<CancellationToken>()))
            .Returns(outcome);
        return installer.Object;
    }

    private static string EngineFolder(bool withMainPy)
    {
        var root = Path.Combine(Path.GetTempPath(), "dn-engine-tile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        if (withMainPy) File.WriteAllText(Path.Combine(root, "main.py"), "# ComfyUI");
        return root;
    }

    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }
}
