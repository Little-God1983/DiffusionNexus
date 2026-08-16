using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Installer.SDK.DataAccess;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.ConfigurationChecker;
using DiffusionNexus.UI.ViewModels;
using Moq;

namespace DiffusionNexus.Tests.InstallerManager;

/// <summary>
/// Builds an <see cref="InstallerManagerViewModel"/> backed by mocks, for engine-tile tests.
/// </summary>
internal static class EngineTestHarness
{
    public static InstallerManagerViewModel CreateInstallerManagerViewModel(
        IReadOnlyList<InstallerPackage> packages)
    {
        var repo = new Mock<IInstallerPackageRepository>();
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(packages.ToList());

        var uow = new Mock<IUnitOfWork>();
        uow.Setup(u => u.InstallerPackages).Returns(repo.Object);
        uow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        return new InstallerManagerViewModel(
            new Mock<IDialogService>().Object,
            uow.Object,
            new PackageProcessManager(),
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IConfigurationRepository>().Object,
            new Mock<IConfigurationCheckerService>().Object,
            new Mock<IWorkloadInstallService>().Object,
            [],
            new Mock<IUnifiedLogger>().Object);
    }
}
