using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Installer.SDK.DataAccess;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.ConfigurationChecker;
using DiffusionNexus.UI.Services.Engine;
using DiffusionNexus.UI.ViewModels;
using Moq;

namespace DiffusionNexus.Tests.InstallerManager;

/// <summary>
/// Builds an <see cref="InstallerManagerViewModel"/> backed by mocks, for engine-tile tests.
/// </summary>
internal static class EngineTestHarness
{
    public static InstallerManagerViewModel CreateInstallerManagerViewModel(
        IReadOnlyList<InstallerPackage> packages,
        IManagedEngineInstaller? engineInstaller = null,
        string? chosenFolder = null,
        Action<InstallerPackage>? onPackageAdded = null,
        IReadOnlyList<BaseModelFolder>? baseModelFolders = null,
        Mock<IDialogService>? dialogMock = null,
        Mock<IConfigurationRepository>? configurationRepositoryMock = null,
        Action<InstallerPackage>? onPackageUpdated = null,
        Action<InstallerPackage>? onPackageRemoved = null)
    {
        var repo = new Mock<IInstallerPackageRepository>();
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(packages.ToList());
        repo.Setup(r => r.AddAsync(It.IsAny<InstallerPackage>(), It.IsAny<CancellationToken>()))
            .Callback<InstallerPackage, CancellationToken>((p, _) => onPackageAdded?.Invoke(p))
            .Returns(Task.CompletedTask);
        repo.Setup(r => r.Update(It.IsAny<InstallerPackage>()))
            .Callback<InstallerPackage>(p => onPackageUpdated?.Invoke(p));
        repo.Setup(r => r.Remove(It.IsAny<InstallerPackage>()))
            .Callback<InstallerPackage>(p => onPackageRemoved?.Invoke(p));

        var uow = new Mock<IUnitOfWork>();
        uow.Setup(u => u.InstallerPackages).Returns(repo.Object);
        uow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        // Only wired when a test actually supplies folders — otherwise _unitOfWork.AppSettings
        // stays null, and ResolveSharedModelRootsAsync's catch → [] path (relied on by the other
        // engine-install tests) keeps working exactly as before.
        if (baseModelFolders is not null)
        {
            var appSettingsRepo = new Mock<IAppSettingsRepository>();
            appSettingsRepo.Setup(r => r.GetSettingsWithIncludesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AppSettings { BaseModelFolders = baseModelFolders.ToList() });
            uow.Setup(u => u.AppSettings).Returns(appSettingsRepo.Object);
        }

        // Only apply the default chosenFolder stub when the caller didn't bring their own dialog
        // mock — a caller-supplied mock configures ShowOpenFolderDialogAsync itself (e.g. to throw
        // on the first call), and this default setup would otherwise be applied afterwards and
        // silently win over theirs (last Setup on the same matcher takes precedence in Moq).
        var dialog = dialogMock ?? new Mock<IDialogService>();
        if (dialogMock is null)
            dialog.Setup(d => d.ShowOpenFolderDialogAsync(It.IsAny<string>())).ReturnsAsync(chosenFolder);

        return new InstallerManagerViewModel(
            dialog.Object, uow.Object, new PackageProcessManager(),
            new Mock<IDatasetEventAggregator>().Object,
            (configurationRepositoryMock ?? new Mock<IConfigurationRepository>()).Object,
            new Mock<IConfigurationCheckerService>().Object,
            new Mock<IWorkloadInstallService>().Object,
            [], new Mock<IUnifiedLogger>().Object,
            engineInstaller: engineInstaller);
    }
}
