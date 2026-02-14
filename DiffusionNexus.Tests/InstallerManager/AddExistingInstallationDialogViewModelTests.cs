using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.InstallerManager;

/// <summary>
/// Unit tests for <see cref="AddExistingInstallationDialogViewModel"/>.
/// </summary>
public class AddExistingInstallationDialogViewModelTests
{
    [Fact]
    public async Task BrowseExecutableCommand_WhenFileNotInFoundExecutables_AddsItAndSelectsIt()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        // Create a bat file that WON'T be scanned (in a subfolder)
        var subDir = Path.Combine(tempDir, "scripts");
        Directory.CreateDirectory(subDir);
        var batFile = Path.Combine(subDir, "custom_run.bat");
        File.WriteAllText(batFile, "echo test");

        var mockDialog = new Mock<IDialogService>();
        mockDialog
            .Setup(d => d.ShowOpenFileDialogAsync("Select Executable", tempDir, "*.bat"))
            .ReturnsAsync(batFile);

        var vm = new AddExistingInstallationDialogViewModel(tempDir, mockDialog.Object);

        // The scanned list should NOT contain the file from the subfolder
        vm.FoundExecutables.Should().NotContain("custom_run.bat");

        // Act
        await vm.BrowseExecutableCommand.ExecuteAsync(null);

        // Assert - the file should be added to the list and selected
        vm.FoundExecutables.Should().Contain("custom_run.bat");
        vm.SelectedExecutable.Should().Be("custom_run.bat");
    }

    [Fact]
    public async Task BrowseExecutableCommand_WhenFileAlreadyInFoundExecutables_SelectsWithoutDuplicate()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        var batFile = Path.Combine(tempDir, "run.bat");
        File.WriteAllText(batFile, "echo test");

        var mockDialog = new Mock<IDialogService>();
        mockDialog
            .Setup(d => d.ShowOpenFileDialogAsync("Select Executable", tempDir, "*.bat"))
            .ReturnsAsync(batFile);

        var vm = new AddExistingInstallationDialogViewModel(tempDir, mockDialog.Object);

        // The scanned list should contain the file
        vm.FoundExecutables.Should().Contain("run.bat");
        var countBefore = vm.FoundExecutables.Count;

        // Act
        await vm.BrowseExecutableCommand.ExecuteAsync(null);

        // Assert - no duplicate, still selected
        vm.FoundExecutables.Count.Should().Be(countBefore);
        vm.SelectedExecutable.Should().Be("run.bat");
    }

    [Fact]
    public async Task BrowseExecutableCommand_WhenFileOutsideInstallPath_AddsFullPathAndSelectsIt()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        var otherDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(otherDir);
        var batFile = Path.Combine(otherDir, "external.bat");
        File.WriteAllText(batFile, "echo test");

        var mockDialog = new Mock<IDialogService>();
        mockDialog
            .Setup(d => d.ShowOpenFileDialogAsync("Select Executable", tempDir, "*.bat"))
            .ReturnsAsync(batFile);

        var vm = new AddExistingInstallationDialogViewModel(tempDir, mockDialog.Object);

        // Act
        await vm.BrowseExecutableCommand.ExecuteAsync(null);

        // Assert - full path should be added and selected
        vm.FoundExecutables.Should().Contain(batFile);
        vm.SelectedExecutable.Should().Be(batFile);
    }
}
