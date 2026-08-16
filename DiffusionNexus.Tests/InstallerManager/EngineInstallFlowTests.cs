using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Shared;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.InstallerManager;

public class EngineInstallFlowTests
{
    [Fact]
    public async Task SuccessfulInstall_PersistsAnAppManagedComfyUiRow()
    {
        InstallerPackage? saved = null;

        var installer = new Mock<IManagedEngineInstaller>();
        installer.Setup(i => i.InstallBaseEngineAsync(
                It.IsAny<EngineInstallRequest>(), It.IsAny<IProgress<InstallLogEntry>>(),
                It.IsAny<IProgress<InstallationProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineInstallOutcome(true, false, "done", @"C:\Engine\ComfyUI"));

        var vm = EngineTestHarness.CreateInstallerManagerViewModel(
            packages: [],
            engineInstaller: installer.Object,
            chosenFolder: @"C:\Engine\ComfyUI",
            onPackageAdded: p => saved = p);

        vm.IsEngineTileVisible = true;
        await vm.LoadInstallationsCommand.ExecuteAsync(null);
        await vm.InstallEngineAsync();

        saved.Should().NotBeNull();
        saved!.IsAppManaged.Should().BeTrue();
        saved.Type.Should().Be(InstallerType.ComfyUI);
        saved.InstallationPath.Should().Be(@"C:\Engine\ComfyUI");
        vm.InstallerCards.Single(c => c.IsEngine).IsEngineInstalled.Should().BeTrue();
    }

    [Fact]
    public async Task CancelledInstall_PersistsNothing()
    {
        InstallerPackage? saved = null;

        var installer = new Mock<IManagedEngineInstaller>();
        installer.Setup(i => i.InstallBaseEngineAsync(
                It.IsAny<EngineInstallRequest>(), It.IsAny<IProgress<InstallLogEntry>>(),
                It.IsAny<IProgress<InstallationProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineInstallOutcome(false, true, "aborted", null));

        var vm = EngineTestHarness.CreateInstallerManagerViewModel(
            packages: [], engineInstaller: installer.Object,
            chosenFolder: @"C:\Engine\ComfyUI", onPackageAdded: p => saved = p);

        vm.IsEngineTileVisible = true;
        await vm.LoadInstallationsCommand.ExecuteAsync(null);
        await vm.InstallEngineAsync();

        saved.Should().BeNull();
        vm.InstallerCards.Single(c => c.IsEngine).IsEngineInstalled.Should().BeFalse();
    }

    [Fact]
    public async Task CancelledFolderPicker_DoesNotStartAnInstall()
    {
        var installer = new Mock<IManagedEngineInstaller>();

        var vm = EngineTestHarness.CreateInstallerManagerViewModel(
            packages: [], engineInstaller: installer.Object,
            chosenFolder: null, onPackageAdded: _ => { });

        vm.IsEngineTileVisible = true;
        await vm.LoadInstallationsCommand.ExecuteAsync(null);
        await vm.InstallEngineAsync();

        installer.Verify(i => i.InstallBaseEngineAsync(
            It.IsAny<EngineInstallRequest>(), It.IsAny<IProgress<InstallLogEntry>>(),
            It.IsAny<IProgress<InstallationProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Install_OrdersSharedModelRoots_StarredDefaultFirstThenByOrder()
    {
        // Deliberately out of "natural" order: the non-default folder appears before the
        // default in raw list order, and a disabled folder sits in between. A naive
        // implementation that just forwards the folders as-stored would fail this.
        var folders = new List<BaseModelFolder>
        {
            new() { FolderPath = @"D:\ModelsA", IsEnabled = true, IsDefault = false, Order = 0 },
            new() { FolderPath = @"F:\ModelsDisabled", IsEnabled = false, IsDefault = false, Order = 1 },
            new() { FolderPath = @"E:\ModelsDefault", IsEnabled = true, IsDefault = true, Order = 5 },
        };

        EngineInstallRequest? capturedRequest = null;

        var installer = new Mock<IManagedEngineInstaller>();
        installer.Setup(i => i.InstallBaseEngineAsync(
                It.IsAny<EngineInstallRequest>(), It.IsAny<IProgress<InstallLogEntry>>(),
                It.IsAny<IProgress<InstallationProgress>>(), It.IsAny<CancellationToken>()))
            .Callback<EngineInstallRequest, IProgress<InstallLogEntry>, IProgress<InstallationProgress>, CancellationToken>(
                (req, _, _, _) => capturedRequest = req)
            .ReturnsAsync(new EngineInstallOutcome(true, false, "done", @"C:\Engine\ComfyUI"));

        var vm = EngineTestHarness.CreateInstallerManagerViewModel(
            packages: [],
            engineInstaller: installer.Object,
            chosenFolder: @"C:\Engine\ComfyUI",
            onPackageAdded: _ => { },
            baseModelFolders: folders);

        vm.IsEngineTileVisible = true;
        await vm.LoadInstallationsCommand.ExecuteAsync(null);
        await vm.InstallEngineAsync();

        capturedRequest.Should().NotBeNull();
        capturedRequest!.SharedModelRoots.Should().Equal(@"E:\ModelsDefault", @"D:\ModelsA");
    }

    [Fact]
    public async Task ToggleEngineVisibility_DuringInstall_DoesNotDetachTheInFlightCardOrDuplicateTheInstall()
    {
        // Regression for the review finding: OnIsEngineTileVisibleChanged used to reload the
        // whole InstallerCards list unconditionally, which would detach the card InstallEngineAsync
        // captured at the top — its progress callbacks and final IsEngineInstalled = true would then
        // land on an orphaned VM, while a freshly-built card's Install button was one click from a
        // second concurrent SDK install into the same folder.
        var installGate = new TaskCompletionSource<EngineInstallOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        var installer = new Mock<IManagedEngineInstaller>();
        installer.Setup(i => i.InstallBaseEngineAsync(
                It.IsAny<EngineInstallRequest>(), It.IsAny<IProgress<InstallLogEntry>>(),
                It.IsAny<IProgress<InstallationProgress>>(), It.IsAny<CancellationToken>()))
            .Returns(installGate.Task);

        var vm = EngineTestHarness.CreateInstallerManagerViewModel(
            packages: [], engineInstaller: installer.Object,
            chosenFolder: @"C:\Engine\ComfyUI", onPackageAdded: _ => { });

        vm.IsEngineTileVisible = true;
        await vm.LoadInstallationsCommand.ExecuteAsync(null);

        var originalCard = vm.InstallerCards.Single(c => c.IsEngine);

        // Not awaited: InstallEngineAsync runs synchronously (all mocked awaits are already-
        // completed tasks) up to the gated InstallBaseEngineAsync call, then suspends there and
        // hands back a pending Task — by this point card.IsEngineInstalling is already true.
        var installTask = vm.InstallEngineAsync();

        originalCard.IsEngineInstalling.Should().BeTrue("the install is now in flight on this card");

        // Toggling the switch mid-install must not reload the list out from under it.
        vm.IsEngineTileVisible = false;

        vm.InstallerCards.Single(c => c.IsEngine).Should().BeSameAs(originalCard,
            "a reload here would detach the card the in-flight install is writing progress to");

        // A second Install call while the first is still running must be a no-op, not a second
        // concurrent SDK install into the same folder.
        await vm.InstallEngineAsync();
        installer.Verify(i => i.InstallBaseEngineAsync(
            It.IsAny<EngineInstallRequest>(), It.IsAny<IProgress<InstallLogEntry>>(),
            It.IsAny<IProgress<InstallationProgress>>(), It.IsAny<CancellationToken>()), Times.Once);

        installGate.SetResult(new EngineInstallOutcome(true, false, "done", @"C:\Engine\ComfyUI"));
        await installTask;

        // The deferred reload (suppressed while installing) fires in InstallEngineAsync's finally
        // once the switch is found to have moved — wait for it to actually finish before asserting.
        await (vm.LoadInstallationsCommand.ExecutionTask ?? Task.CompletedTask);

        originalCard.IsEngineInstalled.Should().BeTrue("the install completed on the same card it started on");
        vm.InstallerCards.Any(c => c.IsEngine).Should().BeFalse(
            "the switch ended up off, and the deferred reload must have caught up to that once the install finished");
    }

    [Fact]
    public async Task SuccessfulInstall_WithExistingStaleAppManagedRow_ReusesItInPlaceInsteadOfDuplicating()
    {
        // Regression for the review finding: a stale app-managed row (folder gone) is exactly
        // what puts the tile back in front of the user with an Install button. Reinstalling used
        // to unconditionally AddAsync a second IsAppManaged row, so the DB ended up with two —
        // and since both the tile's own load path and the Canvas engine-root resolver in
        // App.axaml.cs resolve via a plain FirstOrDefault(p => p.IsAppManaged), the older stale
        // row kept winning: the tile still looked broken and the Canvas backend still resolved
        // to the dead path after a *successful* reinstall.
        var staleRow = new InstallerPackage
        {
            Id = 7,
            Name = "Diffusion Nexus Engine",
            InstallationPath = @"C:\Old\Dead\Engine",
            ExecutablePath = null,
            Type = InstallerType.ComfyUI,
            IsAppManaged = true
        };

        InstallerPackage? added = null;
        var updated = new List<InstallerPackage>();
        var removed = new List<InstallerPackage>();

        var installer = new Mock<IManagedEngineInstaller>();
        installer.Setup(i => i.InstallBaseEngineAsync(
                It.IsAny<EngineInstallRequest>(), It.IsAny<IProgress<InstallLogEntry>>(),
                It.IsAny<IProgress<InstallationProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineInstallOutcome(true, false, "done", @"C:\Engine\New"));

        var vm = EngineTestHarness.CreateInstallerManagerViewModel(
            packages: [staleRow],
            engineInstaller: installer.Object,
            chosenFolder: @"C:\Engine\New",
            onPackageAdded: p => added = p,
            onPackageUpdated: p => updated.Add(p),
            onPackageRemoved: p => removed.Add(p));

        vm.IsEngineTileVisible = true;
        await vm.LoadInstallationsCommand.ExecuteAsync(null);
        await vm.InstallEngineAsync();

        added.Should().BeNull("reusing the existing app-managed row must not also insert a new one");
        updated.Should().ContainSingle().Which.Should().BeSameAs(staleRow,
            "the existing row should be updated in place, keeping its Id (and any FK-linked settings)");
        removed.Should().BeEmpty("there was only ever one stale row to reconcile");

        staleRow.InstallationPath.Should().Be(@"C:\Engine\New");
        staleRow.IsAppManaged.Should().BeTrue();
        vm.InstallerCards.Single(c => c.IsEngine).IsEngineInstalled.Should().BeTrue();
    }

    [Fact]
    public async Task FolderPickerThrows_ClearsInFlightFlag_SoALaterCallCanStillInstall()
    {
        // Regression: _isEngineInstallInFlight used to be set before awaiting
        // ShowOpenFolderDialogAsync, outside any try/finally. If the dialog threw, the flag was
        // never cleared, so every later Install click silently no-opped for the rest of the
        // session (and the Canvas visibility toggle stopped reloading the card list).
        var callCount = 0;
        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowOpenFolderDialogAsync(It.IsAny<string>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount == 1)
                    throw new InvalidOperationException("folder picker boom");
                return Task.FromResult<string?>(@"C:\Engine\ComfyUI");
            });

        var installer = new Mock<IManagedEngineInstaller>();
        installer.Setup(i => i.InstallBaseEngineAsync(
                It.IsAny<EngineInstallRequest>(), It.IsAny<IProgress<InstallLogEntry>>(),
                It.IsAny<IProgress<InstallationProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineInstallOutcome(true, false, "done", @"C:\Engine\ComfyUI"));

        var vm = EngineTestHarness.CreateInstallerManagerViewModel(
            packages: [], engineInstaller: installer.Object,
            chosenFolder: null, onPackageAdded: _ => { },
            dialogMock: dialog);

        vm.IsEngineTileVisible = true;
        await vm.LoadInstallationsCommand.ExecuteAsync(null);

        var firstAttempt = () => vm.InstallEngineAsync();
        await firstAttempt.Should().ThrowAsync<InvalidOperationException>(
            "the folder picker's exception is not swallowed, only the in-flight flag is reset");

        await vm.InstallEngineAsync();

        installer.Verify(i => i.InstallBaseEngineAsync(
            It.IsAny<EngineInstallRequest>(), It.IsAny<IProgress<InstallLogEntry>>(),
            It.IsAny<IProgress<InstallationProgress>>(), It.IsAny<CancellationToken>()), Times.Once,
            "the second call must actually reach the installer instead of being silently skipped by a stuck flag");
    }
}
