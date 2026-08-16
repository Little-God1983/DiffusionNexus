using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Shared;
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
}
