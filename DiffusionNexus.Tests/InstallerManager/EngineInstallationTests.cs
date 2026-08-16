using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.InstallerManager;

/// <summary>
/// Tests for the app-managed ("Diffusion Nexus Engine") installation record.
/// </summary>
public class EngineInstallationTests
{
    [Fact]
    public void InstallerPackage_DefaultsToNotAppManaged()
    {
        var package = new InstallerPackage
        {
            Name = "ComfyUI",
            InstallationPath = @"C:\ComfyUI",
            ExecutablePath = null,
            Type = InstallerType.ComfyUI
        };

        package.IsAppManaged.Should().BeFalse(
            "installations the user added by hand must never be treated as engine-owned");
    }

    [Fact]
    public void InstallerPackage_CanBeMarkedAppManaged()
    {
        var package = new InstallerPackage
        {
            Name = "Diffusion Nexus Engine",
            InstallationPath = @"C:\Engine\ComfyUI",
            ExecutablePath = null,
            Type = InstallerType.ComfyUI,
            IsAppManaged = true
        };

        package.IsAppManaged.Should().BeTrue();
    }

    [Fact]
    public void CreateEngineCard_WithoutInstall_OffersInstallOnly()
    {
        var card = InstallerPackageCardViewModel.CreateEngineCard(null);

        card.IsEngine.Should().BeTrue();
        card.IsEngineInstalled.Should().BeFalse();
        card.Name.Should().Be("Diffusion Nexus Engine");
        card.ShowEngineInstallButton.Should().BeTrue();
        card.ShowEngineWorkloadsButton.Should().BeFalse();
        card.ShowLaunchButton.Should().BeFalse("the engine is not a user-launchable app");
    }

    [Fact]
    public void CreateEngineCard_WithInstall_OffersWorkloadsOnly()
    {
        var package = new InstallerPackage
        {
            Id = 0,
            Name = "Diffusion Nexus Engine",
            InstallationPath = @"C:\Engine\ComfyUI",
            ExecutablePath = null,
            Type = InstallerType.ComfyUI,
            IsAppManaged = true
        };

        var card = InstallerPackageCardViewModel.CreateEngineCard(package);

        card.IsEngine.Should().BeTrue();
        card.IsEngineInstalled.Should().BeTrue();
        card.InstallationPath.Should().Be(@"C:\Engine\ComfyUI");
        card.ShowEngineInstallButton.Should().BeFalse();
        card.ShowEngineWorkloadsButton.Should().BeTrue();
    }

    [Fact]
    public void EngineCard_WhileInstalling_HidesInstallButton()
    {
        var card = InstallerPackageCardViewModel.CreateEngineCard(null);

        card.IsEngineInstalling = true;

        card.ShowEngineInstallButton.Should().BeFalse(
            "a second install must not be startable while one is running");
    }

    [Fact]
    public async Task LoadInstallations_HidesAppManagedRowsFromTheOrdinaryList()
    {
        var packages = new List<InstallerPackage>
        {
            new() { Id = 1, Name = "My ComfyUI", InstallationPath = @"C:\A", ExecutablePath = null,
                    Type = InstallerType.ComfyUI },
            new() { Id = 2, Name = "Diffusion Nexus Engine", InstallationPath = @"C:\Engine", ExecutablePath = null,
                    Type = InstallerType.ComfyUI, IsAppManaged = true }
        };

        var vm = EngineTestHarness.CreateInstallerManagerViewModel(packages);
        vm.IsEngineTileVisible = true;

        await vm.LoadInstallationsCommand.ExecuteAsync(null);

        vm.InstallerCards.Should().HaveCount(3, "Core tile + engine tile + the one user installation");
        vm.InstallerCards.Count(c => c.IsEngine).Should().Be(1);
        vm.InstallerCards.Count(c => !c.IsCore && !c.IsEngine).Should().Be(1);
        vm.InstallerCards.Single(c => c.IsEngine).IsEngineInstalled.Should().BeTrue();
    }

    [Fact]
    public async Task LoadInstallations_WhenEngineTileNotVisible_OmitsTheEngineCardEntirely()
    {
        var packages = new List<InstallerPackage>
        {
            new() { Id = 1, Name = "My ComfyUI", InstallationPath = @"C:\A", ExecutablePath = null,
                    Type = InstallerType.ComfyUI },
            new() { Id = 2, Name = "Diffusion Nexus Engine", InstallationPath = @"C:\Engine", ExecutablePath = null,
                    Type = InstallerType.ComfyUI, IsAppManaged = true }
        };

        var vm = EngineTestHarness.CreateInstallerManagerViewModel(packages);
        vm.IsEngineTileVisible = false;

        await vm.LoadInstallationsCommand.ExecuteAsync(null);

        vm.InstallerCards.Any(c => c.IsEngine).Should().BeFalse(
            "the engine tile must not render at all while the feature switch is off");
    }
}
