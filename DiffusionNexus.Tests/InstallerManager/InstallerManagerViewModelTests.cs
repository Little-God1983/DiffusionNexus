using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.InstallerManager;

/// <summary>
/// Unit tests for <see cref="InstallerManagerViewModel"/>.
/// </summary>
public class InstallerManagerViewModelTests
{
    [Fact]
    public async Task AddExistingInstallationAsync_WithOutputFolder_PublishesSettingsSaved()
    {
        // Arrange
        var settingsSavedCount = 0;

        var mockDialog = new Mock<IDialogService>();
        mockDialog.Setup(d => d.ShowOpenFolderDialogAsync(It.IsAny<string>()))
            .ReturnsAsync(@"C:\TestInstall");
        mockDialog.Setup(d => d.ShowAddExistingInstallationDialogAsync(It.IsAny<string>()))
            .ReturnsAsync(new AddExistingInstallationResult(
                "TestInstall",
                @"C:\TestInstall",
                InstallerType.ComfyUI,
                "main.py",
                @"C:\TestInstall\output"));

        var mockRepo = new Mock<IInstallerPackageRepository>();
        mockRepo.Setup(r => r.AddAsync(It.IsAny<InstallerPackage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mockAppSettings = new Mock<IAppSettingsRepository>();
        mockAppSettings.Setup(r => r.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppSettings { Id = 1 });
        mockAppSettings.Setup(r => r.GetSettingsWithIncludesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppSettings { Id = 1, ImageGalleries = [] });
        mockAppSettings.Setup(r => r.AddImageGalleryAsync(It.IsAny<ImageGallery>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mockUow = new Mock<IUnitOfWork>();
        mockUow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var mockProcessManager = new PackageProcessManager();

        var mockEventAggregator = new Mock<IDatasetEventAggregator>();
        mockEventAggregator
            .Setup(e => e.PublishSettingsSaved(It.IsAny<SettingsSavedEventArgs>()))
            .Callback(() => settingsSavedCount++);

        var vm = new InstallerManagerViewModel(
            mockDialog.Object,
            mockRepo.Object,
            mockAppSettings.Object,
            mockUow.Object,
            mockProcessManager,
            mockEventAggregator.Object);

        // Act
        await vm.AddExistingInstallationCommand.ExecuteAsync(null);

        // Assert
        settingsSavedCount.Should().Be(1, "SettingsSaved should be published once when a gallery is linked");
        mockAppSettings.Verify(r => r.AddImageGalleryAsync(It.IsAny<ImageGallery>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddExistingInstallationAsync_WithoutOutputFolder_DoesNotPublishSettingsSaved()
    {
        // Arrange
        var mockDialog = new Mock<IDialogService>();
        mockDialog.Setup(d => d.ShowOpenFolderDialogAsync(It.IsAny<string>()))
            .ReturnsAsync(@"C:\TestInstall");
        mockDialog.Setup(d => d.ShowAddExistingInstallationDialogAsync(It.IsAny<string>()))
            .ReturnsAsync(new AddExistingInstallationResult(
                "TestInstall",
                @"C:\TestInstall",
                InstallerType.ComfyUI,
                "main.py",
                string.Empty));

        var mockRepo = new Mock<IInstallerPackageRepository>();
        mockRepo.Setup(r => r.AddAsync(It.IsAny<InstallerPackage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mockUow = new Mock<IUnitOfWork>();
        mockUow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var mockProcessManager = new PackageProcessManager();
        var mockAppSettings = new Mock<IAppSettingsRepository>();
        var mockEventAggregator = new Mock<IDatasetEventAggregator>();

        var vm = new InstallerManagerViewModel(
            mockDialog.Object,
            mockRepo.Object,
            mockAppSettings.Object,
            mockUow.Object,
            mockProcessManager,
            mockEventAggregator.Object);

        // Act
        await vm.AddExistingInstallationCommand.ExecuteAsync(null);

        // Assert
        mockEventAggregator.Verify(
            e => e.PublishSettingsSaved(It.IsAny<SettingsSavedEventArgs>()),
            Times.Never,
            "SettingsSaved should not be published when no output folder is specified");
    }
}
