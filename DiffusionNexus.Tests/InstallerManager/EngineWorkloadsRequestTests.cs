using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Installer.SDK.DataAccess;
using DiffusionNexus.UI.Services;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.InstallerManager;

/// <summary>
/// Covers the engine branch of <c>InstallerManagerViewModel.OnWorkloadsRequestedAsync</c>.
/// </summary>
/// <remarks>
/// Only the "not installed" refusal path is exercised here. The "installed" path calls
/// <c>ShowWorkloadsDialogAsync</c>, which unconditionally constructs a real
/// <c>Views.Dialogs.WorkloadsDialog</c> (an Avalonia <c>Window</c>) inside its try block — there
/// is no seam to intercept that construction, and this suite must never initialize Avalonia.
/// See the Task 7 fix report for the documented gap.
/// </remarks>
public class EngineWorkloadsRequestTests
{
    [Fact]
    public async Task EngineNotInstalled_ShowsRefusalMessage_AndNeverAttemptsToLoadWorkloads()
    {
        var dialog = new Mock<IDialogService>();
        var configRepo = new Mock<IConfigurationRepository>();

        // No "Diffusion Nexus Engine" row among the packages -> the engine card starts
        // uninstalled (IsEngineInstalled = false, InstallationPath empty).
        var vm = EngineTestHarness.CreateInstallerManagerViewModel(
            packages: [],
            dialogMock: dialog,
            configurationRepositoryMock: configRepo);

        vm.IsEngineTileVisible = true;
        await vm.LoadInstallationsCommand.ExecuteAsync(null);

        var engineCard = vm.InstallerCards.Single(c => c.IsEngine);
        engineCard.IsEngineInstalled.Should().BeFalse();

        await engineCard.ShowWorkloadsCommand.ExecuteAsync(null);

        dialog.Verify(d => d.ShowMessageAsync(
                "Diffusion Nexus Engine",
                "Install the engine first — workloads are installed into it."),
            Times.Once);

        // Confirms the refusal returns before ever constructing a WorkloadsViewModel /
        // attempting to open the (Avalonia) workloads dialog.
        configRepo.Verify(r => r.GetAllAsync(It.IsAny<CancellationToken>()), Times.Never);
        dialog.Verify(d => d.ShowMessageAsync("Error", It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task EngineRowWithBlankInstallationPath_StillShowsRefusalMessage()
    {
        // Belt-and-braces: a blank InstallationPath can never "look installed" (LooksInstalled
        // short-circuits on a blank path), so IsEngineInstalled is false here too — but the
        // refusal guard also checks InstallationPath directly, so a dialog is never opened
        // against an empty path even if IsEngineInstalled were somehow true.
        var dialog = new Mock<IDialogService>();
        var configRepo = new Mock<IConfigurationRepository>();

        var packages = new List<InstallerPackage>
        {
            new()
            {
                Id = 1,
                Name = "Diffusion Nexus Engine",
                InstallationPath = string.Empty,
                ExecutablePath = null,
                Type = InstallerType.ComfyUI,
                IsAppManaged = true
            }
        };

        var vm = EngineTestHarness.CreateInstallerManagerViewModel(
            packages: packages,
            dialogMock: dialog,
            configurationRepositoryMock: configRepo);

        vm.IsEngineTileVisible = true;
        await vm.LoadInstallationsCommand.ExecuteAsync(null);

        var engineCard = vm.InstallerCards.Single(c => c.IsEngine);
        engineCard.IsEngineInstalled.Should().BeFalse("a blank path can never look installed on disk");
        engineCard.InstallationPath.Should().BeEmpty();

        await engineCard.ShowWorkloadsCommand.ExecuteAsync(null);

        dialog.Verify(d => d.ShowMessageAsync(
                "Diffusion Nexus Engine",
                "Install the engine first — workloads are installed into it."),
            Times.Once);
        configRepo.Verify(r => r.GetAllAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
