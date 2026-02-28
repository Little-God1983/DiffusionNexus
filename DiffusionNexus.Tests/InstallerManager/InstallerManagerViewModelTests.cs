using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Installer.SDK.DataAccess;
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

        var mockConfigRepo = new Mock<IConfigurationRepository>();

        var vm = new InstallerManagerViewModel(
            mockDialog.Object,
            mockRepo.Object,
            mockAppSettings.Object,
            mockUow.Object,
            mockProcessManager,
            mockEventAggregator.Object,
            mockConfigRepo.Object);

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
        var mockConfigRepo = new Mock<IConfigurationRepository>();

        var vm = new InstallerManagerViewModel(
            mockDialog.Object,
            mockRepo.Object,
            mockAppSettings.Object,
            mockUow.Object,
            mockProcessManager,
            mockEventAggregator.Object,
            mockConfigRepo.Object);

        // Act
        await vm.AddExistingInstallationCommand.ExecuteAsync(null);

        // Assert
        mockEventAggregator.Verify(
            e => e.PublishSettingsSaved(It.IsAny<SettingsSavedEventArgs>()),
            Times.Never,
            "SettingsSaved should not be published when no output folder is specified");
    }

    [Fact]
    public async Task MakeDefault_SetsIsDefaultOnCardAndClearsOthersOfSameType()
    {
        // Arrange
        var comfyPackage1 = new InstallerPackage
        {
            Id = 1, Name = "ComfyUI A", Type = InstallerType.ComfyUI,
            InstallationPath = @"C:\A", ExecutablePath = "main.py", IsDefault = true
        };
        var comfyPackage2 = new InstallerPackage
        {
            Id = 2, Name = "ComfyUI B", Type = InstallerType.ComfyUI,
            InstallationPath = @"C:\B", ExecutablePath = "main.py", IsDefault = false
        };
        var forgePackage = new InstallerPackage
        {
            Id = 3, Name = "Forge", Type = InstallerType.Forge,
            InstallationPath = @"C:\F", ExecutablePath = "webui.bat", IsDefault = true
        };

        var mockDialog = new Mock<IDialogService>();
        var mockRepo = new Mock<IInstallerPackageRepository>();
        mockRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([comfyPackage1, comfyPackage2, forgePackage]);
        mockRepo.Setup(r => r.GetByIdAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(comfyPackage2);
        mockRepo.Setup(r => r.ClearDefaultByTypeAsync(InstallerType.ComfyUI, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mockAppSettings = new Mock<IAppSettingsRepository>();
        var mockUow = new Mock<IUnitOfWork>();
        mockUow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        var mockProcessManager = new PackageProcessManager();
        var mockEventAggregator = new Mock<IDatasetEventAggregator>();
        var mockConfigRepo = new Mock<IConfigurationRepository>();

        var vm = new InstallerManagerViewModel(
            mockDialog.Object,
            mockRepo.Object,
            mockAppSettings.Object,
            mockUow.Object,
            mockProcessManager,
            mockEventAggregator.Object,
            mockConfigRepo.Object);

        // Load cards
        await vm.LoadInstallationsCommand.ExecuteAsync(null);
        vm.InstallerCards.Should().HaveCount(3);

        var cardToMakeDefault = vm.InstallerCards.First(c => c.Id == 2);

        // Act
        await cardToMakeDefault.MakeDefaultCommand.ExecuteAsync(null);

        // Assert
        mockRepo.Verify(r => r.ClearDefaultByTypeAsync(InstallerType.ComfyUI, It.IsAny<CancellationToken>()), Times.Once);
        mockUow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);

        vm.InstallerCards.First(c => c.Id == 1).IsDefault.Should().BeFalse("previous ComfyUI default should be cleared");
        vm.InstallerCards.First(c => c.Id == 2).IsDefault.Should().BeTrue("selected card should be marked as default");
        vm.InstallerCards.First(c => c.Id == 3).IsDefault.Should().BeTrue("Forge default should be unchanged");
    }
}
