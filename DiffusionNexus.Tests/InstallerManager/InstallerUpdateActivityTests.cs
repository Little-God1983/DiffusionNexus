using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.ConfigurationChecker;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.InstallerManager;

/// <summary>#618: a package update says it is running, so the catalog apply waits; it must never stay "running".</summary>
public sealed class InstallerUpdateActivityTests
{
    private readonly InstallActivity _activity = new();
    private readonly Mock<IInstallerUpdateService> _updates = new();

    private InstallerManagerViewModel Vm() => new(
        new Mock<IDialogService>().Object, new Mock<IUnitOfWork>().Object, new PackageProcessManager(),
        new Mock<IDatasetEventAggregator>().Object, new Mock<ICatalog>().Object,
        new Mock<IConfigurationCheckerService>().Object, new Mock<IWorkloadInstallService>().Object,
        [_updates.Object], new Mock<IUnifiedLogger>().Object, installActivity: _activity);

    private static InstallerPackageCardViewModel Card() =>
        new(new InstallerPackage { Id = 1, Name = "ComfyUI", Type = InstallerType.ComfyUI, InstallationPath = @"C:\ComfyUI", ExecutablePath = null });

    public InstallerUpdateActivityTests()
    {
        _updates.SetupGet(u => u.SupportedTypes).Returns(new HashSet<InstallerType> { InstallerType.ComfyUI });
        _updates.Setup(u => u.CheckForUpdatesAsync(It.IsAny<string>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new UpdateCheckResult(true, "a", "b", "1 commit"));
    }

    [Fact]
    public async Task An_update_is_running_while_it_runs_and_ends_with_it()
    {
        string? seen = null;
        _updates.Setup(u => u.UpdateAsync(It.IsAny<string>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
                .Callback(() => seen = _activity.Running)
                .ReturnsAsync(new UpdateResult(true, null, "done"));

        await Vm().OnUpdateRequestedAsync(Card());

        seen.Should().Be("ComfyUI");
        _activity.Running.Should().BeNull();
    }

    // Review M9: a throwing "update started" subscriber left the activity running for the session,
    // so the catalog apply said "It can be applied once ComfyUI has finished." forever.
    [Fact]
    public async Task A_throwing_update_subscriber_never_leaves_the_update_running()
    {
        var vm = Vm();
        vm.InstallerUpdateStateChanged += (_, e) => { if (e.IsUpdating) throw new InvalidOperationException("console tab gone"); };

        try { await vm.OnUpdateRequestedAsync(Card()); } catch (InvalidOperationException) { }

        _activity.Running.Should().BeNull();
    }
}
