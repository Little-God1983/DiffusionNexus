using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
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
}
