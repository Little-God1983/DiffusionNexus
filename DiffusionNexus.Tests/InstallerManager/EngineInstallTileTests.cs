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
    public async Task OnlyErrorsCountAsErrors_StderrAndCommandsStayInfo_AndTheLatestShowsAsOneActivityLine()
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
                    // The SDK reports every stderr line as Warning, and git writes its progress there.
                    log.Report(new InstallLogEntry { Message = "Cloning into 'ComfyUI'...", Level = SdkLogLevel.Warning });
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
        logger.Verify(l => l.Info(LogCategory.Installation, "Diffusion Nexus Engine", "Cloning into 'ComfyUI'...", null), Times.Once,
            "stderr is not a problem: a clean clone must not count as warnings");
        logger.Verify(l => l.Warn(It.IsAny<LogCategory>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        logger.Verify(l => l.Info(LogCategory.Installation, "Diffusion Nexus Engine", "Executing: pip install torch", null), Times.Once,
            "the console hides Debug by default, and the command is what names a hanging step");
        logger.Verify(l => l.Info(LogCategory.Installation, "Diffusion Nexus Engine", "verbose detail", null), Times.Once);
        logger.Verify(l => l.Error(LogCategory.Installation, "Diffusion Nexus Engine", "python crashed", null), Times.Once);
        card.EngineActivity.Should().Be("python crashed",
            "the tile shows the latest line that is not a command or verbose detail (Debug, Trace stay in the console)");

        gate.SetResult(new EngineInstallOutcome(true, false, "done", @"C:\Engine\New"));
        await install;
        card.EngineActivity.Should().BeNull();
    }

    [Fact]
    public async Task FailedReinstall_ThatLeftAComfyUiEntryPoint_ReadsTheFolderLikeAReload()
    {
        // The row's folder exists but holds no engine; the reinstall into it clones ComfyUI (main.py)
        // and then fails. The tile must read the folder the way a reload would, not differently.
        var root = EngineFolder(withMainPy: false);
        try
        {
            var row = StaleRow();
            row.InstallationPath = root;
            var installer = new Mock<IManagedEngineInstaller>();
            installer.Setup(i => i.InstallBaseEngineAsync(
                    It.IsAny<EngineInstallRequest>(), It.IsAny<IProgress<InstallLogEntry>>(),
                    It.IsAny<IProgress<InstallationProgress>>(), It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    File.WriteAllText(Path.Combine(root, "main.py"), "# ComfyUI");
                    return Task.FromResult(new EngineInstallOutcome(false, false, "torch failed", null));
                });
            var vm = EngineTestHarness.CreateInstallerManagerViewModel(
                [row], engineInstaller: installer.Object, chosenFolder: root, onPackageAdded: _ => { });
            vm.IsEngineTileVisible = true;
            await vm.LoadInstallationsCommand.ExecuteAsync(null);
            var card = vm.InstallerCards.Single(c => c.IsEngine);
            card.VersionDisplay.Should().Be("Installation incomplete");

            await vm.InstallEngineAsync();

            var reloaded = InstallerPackageCardViewModel.CreateEngineCard(row);
            card.VersionDisplay.Should().Be(reloaded.VersionDisplay);
            card.IsEngineInstalled.Should().Be(reloaded.IsEngineInstalled);
            card.ShowMissingBanner.Should().Be(reloaded.ShowMissingBanner);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ANewStep_ClearsThePreviousStepsActivityLine()
    {
        var gate = new TaskCompletionSource<EngineInstallOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var installer = new Mock<IManagedEngineInstaller>();
        installer.Setup(i => i.InstallBaseEngineAsync(
                It.IsAny<EngineInstallRequest>(), It.IsAny<IProgress<InstallLogEntry>>(),
                It.IsAny<IProgress<InstallationProgress>>(), It.IsAny<CancellationToken>()))
            .Returns<EngineInstallRequest, IProgress<InstallLogEntry>, IProgress<InstallationProgress>, CancellationToken>(
                (_, log, steps, _) =>
                {
                    steps.Report(new InstallationProgress { CurrentStep = InstallationStep.PythonCheck, StepIndex = 4, TotalSteps = 13, Message = "Installing requirements" });
                    log.Report(new InstallLogEntry { Message = "Successfully installed requests-2.32.3" });
                    steps.Report(new InstallationProgress { CurrentStep = InstallationStep.PythonCheck, StepIndex = 5, TotalSteps = 13, Message = "Installing PyTorch..." });
                    return gate.Task;
                });
        var vm = EngineTestHarness.CreateInstallerManagerViewModel(
            [], engineInstaller: installer.Object, chosenFolder: @"C:\Engine\New", onPackageAdded: _ => { });
        vm.IsEngineTileVisible = true;
        await vm.LoadInstallationsCommand.ExecuteAsync(null);
        var card = vm.InstallerCards.Single(c => c.IsEngine);

        var install = StartInline(vm);

        card.EngineStatusMessage.Should().Be("[6/13] Installing PyTorch...");
        card.EngineActivity.Should().BeNull("step 5's last line says nothing about step 6");

        gate.SetResult(new EngineInstallOutcome(true, false, "done", @"C:\Engine\New"));
        await install;
    }

    [Fact]
    public async Task OutputThatRedrawsItself_ShowsItsNewestState()
    {
        var gate = new TaskCompletionSource<EngineInstallOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var installer = new Mock<IManagedEngineInstaller>();
        installer.Setup(i => i.InstallBaseEngineAsync(
                It.IsAny<EngineInstallRequest>(), It.IsAny<IProgress<InstallLogEntry>>(),
                It.IsAny<IProgress<InstallationProgress>>(), It.IsAny<CancellationToken>()))
            .Returns<EngineInstallRequest, IProgress<InstallLogEntry>, IProgress<InstallationProgress>, CancellationToken>(
                (_, log, _, _) =>
                {
                    log.Report(new InstallLogEntry { Message = "Receiving objects:   1%\rReceiving objects:  35%\rReceiving objects: 100%, done." });
                    return gate.Task;
                });
        var vm = EngineTestHarness.CreateInstallerManagerViewModel(
            [], engineInstaller: installer.Object, chosenFolder: @"C:\Engine\New", onPackageAdded: _ => { });
        vm.IsEngineTileVisible = true;
        await vm.LoadInstallationsCommand.ExecuteAsync(null);
        var card = vm.InstallerCards.Single(c => c.IsEngine);

        var install = StartInline(vm);

        card.EngineActivity.Should().Be("Receiving objects: 100%, done.",
            "a carriage return redraws the line: the last state is the current one");

        gate.SetResult(new EngineInstallOutcome(true, false, "done", @"C:\Engine\New"));
        await install;
    }

    [Fact]
    public async Task TheCatchUpReload_RunsAfterTheFailureDialog_NotUnderIt()
    {
        var gate = new TaskCompletionSource<EngineInstallOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        InstallerPackageCardViewModel? card = null;
        InstallerManagerViewModel? vm = null;
        bool? cardStillShownAtDialog = null;
        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowOpenFolderDialogAsync(It.IsAny<string>())).ReturnsAsync(@"C:\Engine\New");
        dialog.Setup(d => d.ShowConfirmAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Callback(() => cardStillShownAtDialog = vm!.InstallerCards.Contains(card!))
            .ReturnsAsync(false);
        vm = EngineTestHarness.CreateInstallerManagerViewModel(
            [StaleRow()], engineInstaller: Installer(gate.Task), onPackageAdded: _ => { }, dialogMock: dialog);
        vm.IsEngineTileVisible = true;
        await vm.LoadInstallationsCommand.ExecuteAsync(null);
        card = vm.InstallerCards.Single(c => c.IsEngine);

        var install = vm.InstallEngineAsync();
        vm.IsEngineTileVisible = false;   // its reload waits for the install
        gate.SetResult(new EngineInstallOutcome(false, false, "pip failed", null));
        await install;
        await (vm.LoadInstallationsCommand.ExecutionTask ?? Task.CompletedTask);

        cardStillShownAtDialog.Should().BeTrue("the failure belongs to the tile the user watched; the list catches up after");
        vm.InstallerCards.Any(c => c.IsEngine).Should().BeFalse("the switch ended off, and the reload did catch up");
    }

    [Fact]
    public async Task AMultiLineEntry_ShowsItsLastLine()
    {
        var gate = new TaskCompletionSource<EngineInstallOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var installer = new Mock<IManagedEngineInstaller>();
        installer.Setup(i => i.InstallBaseEngineAsync(
                It.IsAny<EngineInstallRequest>(), It.IsAny<IProgress<InstallLogEntry>>(),
                It.IsAny<IProgress<InstallationProgress>>(), It.IsAny<CancellationToken>()))
            .Returns<EngineInstallRequest, IProgress<InstallLogEntry>, IProgress<InstallationProgress>, CancellationToken>(
                (_, log, _, _) =>
                {
                    log.Report(new InstallLogEntry
                    {
                        Message = "Traceback (most recent call last):\n  File \"main.py\", line 1\nRuntimeError: CUDA error\n",
                        Level = SdkLogLevel.Error
                    });
                    return gate.Task;
                });
        var vm = EngineTestHarness.CreateInstallerManagerViewModel(
            [], engineInstaller: installer.Object, chosenFolder: @"C:\Engine\New", onPackageAdded: _ => { });
        vm.IsEngineTileVisible = true;
        await vm.LoadInstallationsCommand.ExecuteAsync(null);
        var card = vm.InstallerCards.Single(c => c.IsEngine);

        var install = StartInline(vm);

        card.EngineActivity.Should().Be("RuntimeError: CUDA error", "the newest line says what is happening now");

        gate.SetResult(new EngineInstallOutcome(true, false, "done", @"C:\Engine\New"));
        await install;
    }

    [Fact]
    public async Task InstalledEngine_FailedReinstallThatBrokeItsFolder_SaysIncomplete()
    {
        var root = EngineFolder(withMainPy: true);
        try
        {
            var row = StaleRow();
            row.InstallationPath = root;
            var installer = new Mock<IManagedEngineInstaller>();
            installer.Setup(i => i.InstallBaseEngineAsync(
                    It.IsAny<EngineInstallRequest>(), It.IsAny<IProgress<InstallLogEntry>>(),
                    It.IsAny<IProgress<InstallationProgress>>(), It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    File.Delete(Path.Combine(root, "main.py"));
                    return Task.FromResult(new EngineInstallOutcome(false, false, "clone failed", null));
                });
            var vm = EngineTestHarness.CreateInstallerManagerViewModel(
                [row], engineInstaller: installer.Object, chosenFolder: root, onPackageAdded: _ => { });
            vm.IsEngineTileVisible = true;
            await vm.LoadInstallationsCommand.ExecuteAsync(null);
            var card = vm.InstallerCards.Single(c => c.IsEngine);
            card.VersionDisplay.Should().Be("App-managed");

            await vm.InstallEngineAsync();

            card.VersionDisplay.Should().Be("Installation incomplete", "a reload would say so: the folder lost its engine");
            card.IsEngineInstalled.Should().BeFalse();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task StartOfInstallThatThrows_DoesNotLeaveTheTileInstalling()
    {
        var logger = new Mock<IUnifiedLogger>();
        logger.Setup(l => l.Info(LogCategory.Installation, "Diffusion Nexus Engine",
                It.Is<string>(m => m.StartsWith("Starting base engine install")), It.IsAny<string?>()))
            .Throws(new IOException("log file locked"));
        var vm = EngineTestHarness.CreateInstallerManagerViewModel(
            [StaleRow()], engineInstaller: Installer(Task.FromResult(new EngineInstallOutcome(true, false, "done", @"C:\Engine\New"))),
            chosenFolder: @"C:\Engine\New", onPackageAdded: _ => { }, loggerMock: logger);
        vm.IsEngineTileVisible = true;
        await vm.LoadInstallationsCommand.ExecuteAsync(null);
        var card = vm.InstallerCards.Single(c => c.IsEngine);

        try { await vm.InstallEngineAsync(); } catch (IOException) { /* how it surfaces is not the point */ }

        card.IsEngineInstalling.Should().BeFalse("a tile stuck on \"Installing...\" hides Install for the rest of the session");
        card.ShowEngineInstallButton.Should().BeTrue();
    }

    // Runs the install with Progress<T> callbacks inline, so reports made before the install
    // suspends on its gate have landed when this returns.
    private static Task StartInline(InstallerManagerViewModel vm)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());
        try { return vm.InstallEngineAsync(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
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
