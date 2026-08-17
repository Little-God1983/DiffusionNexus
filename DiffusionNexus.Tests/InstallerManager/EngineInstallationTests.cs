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
    public void CreateEngineCard_WithInstallOnDisk_OffersWorkloadsOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), "dn-engine-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "main.py"), "# comfy");
        try
        {
            var package = new InstallerPackage
            {
                Id = 0,
                Name = "Diffusion Nexus Engine",
                InstallationPath = root,
                ExecutablePath = null,
                Type = InstallerType.ComfyUI,
                IsAppManaged = true
            };

            var card = InstallerPackageCardViewModel.CreateEngineCard(package);

            card.IsEngine.Should().BeTrue();
            card.IsEngineInstalled.Should().BeTrue();
            card.IsMissing.Should().BeFalse();
            card.InstallationPath.Should().Be(root);
            card.ShowEngineInstallButton.Should().BeFalse();
            card.ShowEngineWorkloadsButton.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CreateEngineCard_RowPresentButFolderGone_OffersInstallNotWorkloads()
    {
        // The database row can outlive the folder: an install can fail after writing the row,
        // or the user can delete the folder by hand. Either way "installed" must be grounded in
        // disk reality (ManagedEngineLocator.LooksInstalled), not the row's mere existence — a
        // stale row must never strand the user on a tile that offers Workloads against a path
        // that no longer exists with no way back to Install.
        var package = new InstallerPackage
        {
            Id = 0,
            Name = "Diffusion Nexus Engine",
            InstallationPath = Path.Combine(Path.GetTempPath(), "dn-engine-missing-" + Guid.NewGuid()),
            ExecutablePath = null,
            Type = InstallerType.ComfyUI,
            IsAppManaged = true
        };

        var card = InstallerPackageCardViewModel.CreateEngineCard(package);

        card.IsEngine.Should().BeTrue();
        card.IsEngineInstalled.Should().BeFalse("the folder backing the row does not exist on disk");
        card.IsMissing.Should().BeTrue();
        card.ShowEngineInstallButton.Should().BeTrue("a missing folder must offer reinstall, not a dead end");
        card.ShowEngineWorkloadsButton.Should().BeFalse();
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
        var engineRoot = Path.Combine(Path.GetTempPath(), "dn-engine-" + Guid.NewGuid());
        Directory.CreateDirectory(engineRoot);
        File.WriteAllText(Path.Combine(engineRoot, "main.py"), "# comfy");
        try
        {
            var packages = new List<InstallerPackage>
            {
                new() { Id = 1, Name = "My ComfyUI", InstallationPath = @"C:\A", ExecutablePath = null,
                        Type = InstallerType.ComfyUI },
                new() { Id = 2, Name = "Diffusion Nexus Engine", InstallationPath = engineRoot, ExecutablePath = null,
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
        finally
        {
            Directory.Delete(engineRoot, recursive: true);
        }
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
