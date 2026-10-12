using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.UI.Services.Engine;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.InstallerManager;

/// <summary>Every item on the Engine and Core tiles either works or says why it can't.</summary>
public class EngineTileMenuTests
{
    private static InstallerPackage EngineRow(string path) => new()
    {
        Id = 7, Name = "Diffusion Nexus Engine", InstallationPath = path, Type = InstallerType.ComfyUI, IsAppManaged = true, ExecutablePath = string.Empty
    };

    [Fact]
    public void Engine_SettingsAndMakeDefault_AreGreyed_WithTheirReasons()
    {
        var card = InstallerPackageCardViewModel.CreateEngineCard(EngineRow(@"C:\Nowhere\Engine"));

        card.OpenSettingsCommand.CanExecute(null).Should().BeFalse();
        card.SettingsTooltip.Should().Be(
            "The app manages the Diffusion Nexus Engine's settings. To install it somewhere else, uninstall it and install it again.");
        card.MakeDefaultCommand.CanExecute(null).Should().BeFalse();
        card.MakeDefaultTooltip.Should().Be(
            "The Engine runs in the background for Diffusion Nexus features, so it can't be your default ComfyUI.");
    }

    [Fact]
    public void Engine_WithARow_OffersUninstall_NotRemoveOrDelete()
    {
        var card = InstallerPackageCardViewModel.CreateEngineCard(EngineRow(@"C:\Nowhere\Engine"));

        card.ShowEngineUninstallMenuItem.Should().BeTrue("a missing folder still has a row to forget");
        card.ShowRemoveMenuItem.Should().BeFalse();
        card.ShowDeleteFromDiskMenuItem.Should().BeFalse();
    }

    [Fact]
    public void Engine_WithoutARow_HasNothingToUninstall()
    {
        InstallerPackageCardViewModel.CreateEngineCard(null).ShowEngineUninstallMenuItem.Should().BeFalse();
    }

    [Fact]
    public void Core_SettingsOpenFolderAndMakeDefault_AreGreyed_WithReasons()
    {
        var card = InstallerPackageCardViewModel.CreateCoreCard();

        card.OpenSettingsCommand.CanExecute(null).Should().BeFalse();
        card.OpenFolderCommand.CanExecute(null).Should().BeFalse();
        card.MakeDefaultCommand.CanExecute(null).Should().BeFalse();
        card.SettingsTooltip.Should().Contain("built into the app");
        card.OpenFolderTooltip.Should().Contain("built into the app");
    }

    [Fact]
    public void Engine_OpenFolder_NoRow_IsGreyed_WithReason()
    {
        var card = InstallerPackageCardViewModel.CreateEngineCard(null);

        card.OpenFolderCommand.CanExecute(null).Should().BeFalse();
        card.OpenFolderTooltip.Should().Be("The Diffusion Nexus Engine is not installed, so there is no folder to open.");
    }

    [Fact]
    public void Engine_OpenFolder_MissingFolder_IsGreyed_WithReason()
    {
        var card = InstallerPackageCardViewModel.CreateEngineCard(EngineRow(@"C:\Nowhere\Engine"));
        card.ApplyEngineFolder(EngineFolderState.Missing);

        card.OpenFolderCommand.CanExecute(null).Should().BeFalse();
        card.OpenFolderTooltip.Should().Be("The Engine's folder is gone. Use Uninstall Engine to remove it from the app, or install it again.");
    }

    [Theory]
    [InlineData(EngineFolderState.Installed)]
    [InlineData(EngineFolderState.Incomplete)]
    public void Engine_OpenFolder_ExistingFolder_IsEnabled(EngineFolderState state)
    {
        var card = InstallerPackageCardViewModel.CreateEngineCard(EngineRow(@"C:\Nowhere\Engine"));
        card.ApplyEngineFolder(EngineFolderState.Missing);
        card.ApplyEngineFolder(state);

        card.OpenFolderCommand.CanExecute(null).Should().BeTrue();
        card.OpenFolderTooltip.Should().BeNull();
    }

    [Fact]
    public void OrdinaryCard_KeepsEveryItem()
    {
        var card = new InstallerPackageCardViewModel(new InstallerPackage
        {
            Id = 3, Name = "My ComfyUI", InstallationPath = @"C:\Comfy", Type = InstallerType.ComfyUI, ExecutablePath = string.Empty
        });

        card.OpenSettingsCommand.CanExecute(null).Should().BeTrue();
        card.SettingsTooltip.Should().Be("Settings");
        card.MakeDefaultCommand.CanExecute(null).Should().BeTrue();
        card.ShowRemoveMenuItem.Should().BeTrue();
        card.ShowEngineUninstallMenuItem.Should().BeFalse();
    }
}
