using DiffusionNexus.Domain.Enums;
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
    #region Validation Tests

    [Fact]
    public void WhenNameIsEmptyThenCanConfirmIsFalse()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "run.bat"), "echo test");

        var vm = new AddExistingInstallationDialogViewModel(tempDir);

        // Act - clear the auto-inferred name
        vm.Name = string.Empty;

        // Assert
        vm.CanConfirm.Should().BeFalse();
        vm.NameError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void WhenExecutableIsEmptyThenCanConfirmIsFalse()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        // No .bat files → SelectedExecutable will be empty
        var vm = new AddExistingInstallationDialogViewModel(tempDir);
        vm.Name = "TestInstall";

        // Assert
        vm.CanConfirm.Should().BeFalse();
        vm.ExecutableError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void WhenAllRequiredFieldsAreFilledThenCanConfirmIsTrue()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "run.bat"), "echo test");

        var vm = new AddExistingInstallationDialogViewModel(tempDir);

        // Act - name is auto-inferred, executable is auto-detected
        // Ensure both are set
        vm.Name = "TestInstall";

        // Assert
        vm.SelectedExecutable.Should().NotBeNullOrEmpty();
        vm.CanConfirm.Should().BeTrue();
        vm.NameError.Should().BeNull();
        vm.ExecutableError.Should().BeNull();
    }

    [Fact]
    public void WhenNameIsClearedThenNameErrorAppears()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "run.bat"), "echo test");

        var vm = new AddExistingInstallationDialogViewModel(tempDir);
        vm.Name = "Something";
        vm.NameError.Should().BeNull();

        // Act
        vm.Name = "";

        // Assert
        vm.NameError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void WhenEditModeWithValidFieldsThenCanConfirmIsTrue()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "run.bat"), "echo test");

        var vm = new AddExistingInstallationDialogViewModel(
            "TestInstall", tempDir, InstallerType.ComfyUI, "run.bat", tempDir);

        // Assert
        vm.CanConfirm.Should().BeTrue();
        vm.NameError.Should().BeNull();
        vm.ExecutableError.Should().BeNull();
    }

    #endregion

    #region Browse Executable Tests
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

    #endregion
}
