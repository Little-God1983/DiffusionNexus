using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Installer.SDK.DataAccess;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.ConfigurationChecker;
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
        mockUow.Setup(u => u.InstallerPackages).Returns(mockRepo.Object);
        mockUow.Setup(u => u.AppSettings).Returns(mockAppSettings.Object);

        var mockProcessManager = new PackageProcessManager();

        var mockEventAggregator = new Mock<IDatasetEventAggregator>();
        mockEventAggregator
            .Setup(e => e.PublishSettingsSaved(It.IsAny<SettingsSavedEventArgs>()))
            .Callback(() => settingsSavedCount++);

        var mockConfigRepo = new Mock<IConfigurationRepository>();
        var mockCheckerService = new Mock<IConfigurationCheckerService>();
        var mockInstallService = new Mock<IWorkloadInstallService>();

        var vm = new InstallerManagerViewModel(
            mockDialog.Object,
            mockUow.Object,
            mockProcessManager,
            mockEventAggregator.Object,
            mockConfigRepo.Object,
            mockCheckerService.Object,
            mockInstallService.Object,
            Enumerable.Empty<IInstallerUpdateService>(),
            new Mock<IUnifiedLogger>().Object);

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
        mockUow.Setup(u => u.InstallerPackages).Returns(mockRepo.Object);

        var mockProcessManager = new PackageProcessManager();
        var mockAppSettings = new Mock<IAppSettingsRepository>();
        mockUow.Setup(u => u.AppSettings).Returns(mockAppSettings.Object);
        var mockEventAggregator = new Mock<IDatasetEventAggregator>();
        var mockConfigRepo = new Mock<IConfigurationRepository>();
        var mockCheckerService = new Mock<IConfigurationCheckerService>();
        var mockInstallService = new Mock<IWorkloadInstallService>();

        var vm = new InstallerManagerViewModel(
            mockDialog.Object,
            mockUow.Object,
            mockProcessManager,
            mockEventAggregator.Object,
            mockConfigRepo.Object,
            mockCheckerService.Object,
            mockInstallService.Object,
            Enumerable.Empty<IInstallerUpdateService>(),
            new Mock<IUnifiedLogger>().Object);

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

        var mockUow = new Mock<IUnitOfWork>();
        mockUow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        mockUow.Setup(u => u.InstallerPackages).Returns(mockRepo.Object);
        var mockProcessManager = new PackageProcessManager();
        var mockEventAggregator = new Mock<IDatasetEventAggregator>();
        var mockConfigRepo = new Mock<IConfigurationRepository>();
        var mockCheckerService = new Mock<IConfigurationCheckerService>();
        var mockInstallService = new Mock<IWorkloadInstallService>();

        var vm = new InstallerManagerViewModel(
            mockDialog.Object,
            mockUow.Object,
            mockProcessManager,
            mockEventAggregator.Object,
            mockConfigRepo.Object,
            mockCheckerService.Object,
            mockInstallService.Object,
            Enumerable.Empty<IInstallerUpdateService>(),
            new Mock<IUnifiedLogger>().Object);

        // Load cards. The list now contains 3 DB-backed packages plus the
        // singleton static "Diffusion Nexus Core" card pinned to the top.
        await vm.LoadInstallationsCommand.ExecuteAsync(null);
        vm.InstallerCards.Should().HaveCount(4);
        vm.InstallerCards.Count(c => c.IsCore).Should().Be(1);

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

    // ── Remove-installation flow (settings cleanup checkboxes) ──

    private sealed class RemoveFlowHarness
    {
        public Mock<IDialogService> Dialog { get; } = new();
        public Mock<IInstallerPackageRepository> PackageRepo { get; } = new();
        public Mock<IAppSettingsRepository> AppSettingsRepo { get; } = new();
        public Mock<IUnitOfWork> Uow { get; } = new();
        public Mock<IDatasetEventAggregator> EventAggregator { get; } = new();
        public InstallerPackage Package { get; }
        public InstallerManagerViewModel Vm { get; }

        public RemoveFlowHarness(AppSettings settings, string? installationPath = null)
        {
            Package = new InstallerPackage
            {
                Id = 5,
                Name = "ComfyUI",
                InstallationPath = installationPath ?? @"C:\AI\ComfyUI",
                ExecutablePath = "main.py",
                Type = InstallerType.ComfyUI,
            };

            PackageRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([Package]);
            PackageRepo.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Package);
            AppSettingsRepo.Setup(r => r.GetSettingsWithIncludesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(settings);
            Uow.Setup(u => u.InstallerPackages).Returns(PackageRepo.Object);
            Uow.Setup(u => u.AppSettings).Returns(AppSettingsRepo.Object);
            Uow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

            Vm = new InstallerManagerViewModel(
                Dialog.Object,
                Uow.Object,
                new PackageProcessManager(),
                EventAggregator.Object,
                new Mock<IConfigurationRepository>().Object,
                new Mock<IConfigurationCheckerService>().Object,
                new Mock<IWorkloadInstallService>().Object,
                Enumerable.Empty<IInstallerUpdateService>(),
                new Mock<IUnifiedLogger>().Object,
                unitOfWorkFactory: () => Uow.Object);
        }

        public async Task<InstallerPackageCardViewModel> LoadCardAsync()
        {
            await Vm.LoadInstallationsCommand.ExecuteAsync(null);
            return Vm.InstallerCards.First(c => c.Id == 5);
        }
    }

    private static AppSettings SettingsWithLinkedRows() => new()
    {
        Id = 1,
        ImageGalleries =
        [
            new ImageGallery { Id = 1, FolderPath = @"D:\Output", InstallerPackageId = 5 },
            new ImageGallery { Id = 2, FolderPath = @"D:\Unrelated" },
        ],
        LoraSources =
        [
            new LoraSource { Id = 1, FolderPath = @"C:\AI\ComfyUI\models\loras" },
            new LoraSource { Id = 2, FolderPath = @"D:\Loras" },
        ],
        BaseModelFolders =
        [
            new BaseModelFolder { Id = 1, FolderPath = @"C:\AI\ComfyUI\models", InstallerPackageId = 5 },
            new BaseModelFolder { Id = 2, FolderPath = @"D:\Models" },
        ],
    };

    [Fact]
    public async Task RemoveInstallation_RemovesLinkedSettingsRows_WhenAllBoxesChecked()
    {
        var harness = new RemoveFlowHarness(SettingsWithLinkedRows());
        RemoveInstallationPrompt? prompt = null;
        harness.Dialog
            .Setup(d => d.ShowRemoveInstallationDialogAsync(It.IsAny<RemoveInstallationPrompt>()))
            .Callback<RemoveInstallationPrompt>(p => prompt = p)
            .ReturnsAsync(new RemoveInstallationResult(true, true, true, true));

        var card = await harness.LoadCardAsync();
        await card.RemoveCommand.ExecuteAsync(null);

        prompt.Should().NotBeNull();
        prompt!.InstallationName.Should().Be("ComfyUI");
        prompt.GalleryFolders.Should().BeEquivalentTo(@"D:\Output");
        prompt.LoraSourceFolders.Should().BeEquivalentTo(@"C:\AI\ComfyUI\models\loras");
        prompt.BaseModelFolders.Should().BeEquivalentTo(@"C:\AI\ComfyUI\models");

        harness.AppSettingsRepo.Verify(r => r.RemoveImageGallery(
            It.Is<ImageGallery>(g => g.Id == 1)), Times.Once);
        harness.AppSettingsRepo.Verify(r => r.RemoveImageGallery(
            It.Is<ImageGallery>(g => g.Id == 2)), Times.Never);
        harness.AppSettingsRepo.Verify(r => r.RemoveLoraSource(
            It.Is<LoraSource>(s => s.Id == 1)), Times.Once);
        harness.AppSettingsRepo.Verify(r => r.RemoveBaseModelFolder(
            It.Is<BaseModelFolder>(f => f.Id == 1)), Times.Once);
        harness.PackageRepo.Verify(r => r.Remove(harness.Package), Times.Once);
        harness.Uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        harness.EventAggregator.Verify(
            e => e.PublishSettingsSaved(It.IsAny<SettingsSavedEventArgs>()), Times.Once);
        harness.Vm.InstallerCards.Should().NotContain(card);
    }

    [Fact]
    public async Task RemoveInstallation_KeepsSettingsRows_WhenBoxesUnchecked()
    {
        var harness = new RemoveFlowHarness(SettingsWithLinkedRows());
        harness.Dialog
            .Setup(d => d.ShowRemoveInstallationDialogAsync(It.IsAny<RemoveInstallationPrompt>()))
            .ReturnsAsync(new RemoveInstallationResult(true, false, false, false));

        var card = await harness.LoadCardAsync();
        await card.RemoveCommand.ExecuteAsync(null);

        harness.AppSettingsRepo.Verify(r => r.RemoveImageGallery(It.IsAny<ImageGallery>()), Times.Never);
        harness.AppSettingsRepo.Verify(r => r.RemoveLoraSource(It.IsAny<LoraSource>()), Times.Never);
        harness.AppSettingsRepo.Verify(r => r.RemoveBaseModelFolder(It.IsAny<BaseModelFolder>()), Times.Never);
        harness.PackageRepo.Verify(r => r.Remove(harness.Package), Times.Once);
        harness.EventAggregator.Verify(
            e => e.PublishSettingsSaved(It.IsAny<SettingsSavedEventArgs>()), Times.Never);
        harness.Vm.InstallerCards.Should().NotContain(card);
    }

    [Fact]
    public async Task RemoveInstallation_Cancelled_RemovesNothing()
    {
        var harness = new RemoveFlowHarness(SettingsWithLinkedRows());
        harness.Dialog
            .Setup(d => d.ShowRemoveInstallationDialogAsync(It.IsAny<RemoveInstallationPrompt>()))
            .ReturnsAsync(RemoveInstallationResult.Cancelled());

        var card = await harness.LoadCardAsync();
        await card.RemoveCommand.ExecuteAsync(null);

        harness.PackageRepo.Verify(r => r.Remove(It.IsAny<InstallerPackage>()), Times.Never);
        harness.Uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        harness.Vm.InstallerCards.Should().Contain(card);
    }

    [Fact]
    public async Task DeleteFromDisk_RemovesLinkedSettingsRows_WithoutAsking()
    {
        // The deleted tree takes its folders with it — dead settings rows are
        // cleaned up unconditionally (no checkboxes on this path).
        var installPath = Path.Combine(Path.GetTempPath(), "dn-delete-test-" + Guid.NewGuid().ToString("N"));
        var settings = new AppSettings
        {
            Id = 1,
            ImageGalleries = [new ImageGallery { Id = 1, FolderPath = @"D:\Output", InstallerPackageId = 5 }],
            LoraSources = [new LoraSource { Id = 1, FolderPath = Path.Combine(installPath, "models", "loras") }],
            BaseModelFolders = [new BaseModelFolder { Id = 1, FolderPath = Path.Combine(installPath, "models"), InstallerPackageId = 5 }],
        };
        var harness = new RemoveFlowHarness(settings, installPath);
        harness.Dialog
            .Setup(d => d.ShowConfirmAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        var card = await harness.LoadCardAsync();
        await card.DeleteFromDiskCommand.ExecuteAsync(null);

        harness.AppSettingsRepo.Verify(r => r.RemoveImageGallery(It.Is<ImageGallery>(g => g.Id == 1)), Times.Once);
        harness.AppSettingsRepo.Verify(r => r.RemoveLoraSource(It.Is<LoraSource>(s => s.Id == 1)), Times.Once);
        harness.AppSettingsRepo.Verify(r => r.RemoveBaseModelFolder(It.Is<BaseModelFolder>(f => f.Id == 1)), Times.Once);
        harness.PackageRepo.Verify(r => r.Remove(harness.Package), Times.Once);
        harness.EventAggregator.Verify(
            e => e.PublishSettingsSaved(It.IsAny<SettingsSavedEventArgs>()), Times.Once);
        harness.Vm.InstallerCards.Should().NotContain(card);
    }
}
