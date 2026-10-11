using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.InstallerManager;

/// <summary>
/// Updating the Engine (its console tile offers Update): no start may run from the folder the update
/// changes, whoever asks for one (#621 review round 3).
/// </summary>
public class EngineUpdateTests
{
    [Fact]
    public async Task UpdatingTheEngine_HoldsTheEngineForMaintenance_ForTheWholeUpdate()
    {
        var root = EngineFolder();
        try
        {
            var maintenance = 0;
            int? maintenanceDuringUpdate = null;
            var engine = new Mock<IManagedComfyUiEngine>();
            engine.Setup(e => e.TryBeginMaintenanceAsync()).ReturnsAsync(() =>
            {
                maintenance++;
                return new DisposeAction(() => maintenance--);
            });
            var service = UpdateService(onUpdate: () => maintenanceDuringUpdate = maintenance);
            int? maintenanceAtResultDialog = null;
            var dialog = new Mock<IDialogService>();
            dialog.Setup(d => d.ShowMessageAsync("Update Complete", It.IsAny<string>()))
                .Callback(() => maintenanceAtResultDialog = maintenance)
                .Returns(Task.CompletedTask);

            var vm = EngineTestHarness.CreateInstallerManagerViewModel(
                [EngineRow(root)], onPackageAdded: _ => { }, dialogMock: dialog, engine: engine.Object, updateServices: [service.Object]);
            vm.IsEngineTileVisible = true;
            await vm.LoadInstallationsCommand.ExecuteAsync(null);

            await vm.UpdatePackageByIdAsync(EngineRowId);

            maintenanceDuringUpdate.Should().Be(1, "a Canvas Generate must not start the Engine from a half-updated folder");
            maintenanceAtResultDialog.Should().Be(0, "the update is over: a dialog left open must not block every start");
            maintenance.Should().Be(0);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ARunningEngine_IsNotUpdatedUnderItself()
    {
        var root = EngineFolder();
        try
        {
            var engine = new Mock<IManagedComfyUiEngine>();
            engine.SetupGet(e => e.BaseUrl).Returns("http://127.0.0.1:50123");
            var service = UpdateService(onUpdate: () => { }, confirmation: "This switches back to the newest release.");
            var dialog = new Mock<IDialogService>();

            var vm = EngineTestHarness.CreateInstallerManagerViewModel(
                [EngineRow(root)], onPackageAdded: _ => { }, dialogMock: dialog, engine: engine.Object, updateServices: [service.Object]);
            vm.IsEngineTileVisible = true;
            await vm.LoadInstallationsCommand.ExecuteAsync(null);

            await vm.UpdatePackageByIdAsync(EngineRowId);

            service.Verify(s => s.UpdateAsync(It.IsAny<string>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
            dialog.Verify(d => d.ShowMessageAsync(It.IsAny<string>(), It.Is<string>(m => m.Contains("Stop the Engine"))), Times.Once);
            dialog.Verify(d => d.ShowConfirmAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never,
                "an update that cannot run asks nothing first");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task AnEngineThatIsStarting_IsNotUpdatedEither()
    {
        // A start boots for up to two minutes with no URL yet; the engine refuses the hold then.
        var root = EngineFolder();
        try
        {
            var engine = new Mock<IManagedComfyUiEngine>();
            engine.Setup(e => e.TryBeginMaintenanceAsync()).ReturnsAsync((IDisposable?)null);
            engine.As<IManagedEngineControl>().SetupGet(e => e.IsStarting).Returns(true);
            var service = UpdateService(onUpdate: () => { }, confirmation: "This switches back to the newest release.");
            var dialog = new Mock<IDialogService>();

            var vm = EngineTestHarness.CreateInstallerManagerViewModel(
                [EngineRow(root)], onPackageAdded: _ => { }, dialogMock: dialog, engine: engine.Object, updateServices: [service.Object]);
            vm.IsEngineTileVisible = true;
            await vm.LoadInstallationsCommand.ExecuteAsync(null);

            await vm.UpdatePackageByIdAsync(EngineRowId);

            service.Verify(s => s.UpdateAsync(It.IsAny<string>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
            dialog.Verify(d => d.ShowMessageAsync(It.IsAny<string>(), It.Is<string>(m => m.Contains("Stop the Engine"))), Times.Once);
            dialog.Verify(d => d.ShowConfirmAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never,
                "a starting Engine is known before the confirmation: an update that cannot run asks nothing");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private const int EngineRowId = 35;

    private static InstallerPackage EngineRow(string root) => new()
    {
        Id = EngineRowId,
        Name = "Diffusion Nexus Engine",
        InstallationPath = root,
        ExecutablePath = null,
        Type = InstallerType.ComfyUI,
        IsAppManaged = true
    };

    private static Mock<IInstallerUpdateService> UpdateService(Action onUpdate, string? confirmation = null)
    {
        var service = new Mock<IInstallerUpdateService>();
        service.SetupGet(s => s.SupportedTypes).Returns(new HashSet<InstallerType> { InstallerType.ComfyUI });
        service.Setup(s => s.CheckForUpdatesAsync(It.IsAny<string>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateCheckResult(true, "aaa", "bbb", "1 commit behind", confirmation));
        service.Setup(s => s.UpdateAsync(It.IsAny<string>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .Callback(onUpdate)
            .ReturnsAsync(new UpdateResult(true, null, "Updated."));
        return service;
    }

    private static string EngineFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "dn-engine-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "main.py"), "# ComfyUI");
        return root;
    }

    private sealed class DisposeAction(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
