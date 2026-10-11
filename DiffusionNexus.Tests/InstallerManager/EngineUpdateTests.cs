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
            engine.Setup(e => e.BeginMaintenance()).Returns(() =>
            {
                maintenance++;
                return new DisposeAction(() => maintenance--);
            });
            var service = UpdateService(onUpdate: () => maintenanceDuringUpdate = maintenance);

            var vm = EngineTestHarness.CreateInstallerManagerViewModel(
                [EngineRow(root)], onPackageAdded: _ => { }, engine: engine.Object, updateServices: [service.Object]);
            vm.IsEngineTileVisible = true;
            await vm.LoadInstallationsCommand.ExecuteAsync(null);

            await vm.UpdatePackageByIdAsync(EngineRowId);

            maintenanceDuringUpdate.Should().Be(1, "a Canvas Generate must not start the Engine from a half-updated folder");
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
            var service = UpdateService(onUpdate: () => { });
            var dialog = new Mock<IDialogService>();

            var vm = EngineTestHarness.CreateInstallerManagerViewModel(
                [EngineRow(root)], onPackageAdded: _ => { }, dialogMock: dialog, engine: engine.Object, updateServices: [service.Object]);
            vm.IsEngineTileVisible = true;
            await vm.LoadInstallationsCommand.ExecuteAsync(null);

            await vm.UpdatePackageByIdAsync(EngineRowId);

            service.Verify(s => s.UpdateAsync(It.IsAny<string>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
            dialog.Verify(d => d.ShowMessageAsync(It.IsAny<string>(), It.Is<string>(m => m.Contains("Stop the Engine"))), Times.Once);
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

    private static Mock<IInstallerUpdateService> UpdateService(Action onUpdate)
    {
        var service = new Mock<IInstallerUpdateService>();
        service.SetupGet(s => s.SupportedTypes).Returns(new HashSet<InstallerType> { InstallerType.ComfyUI });
        service.Setup(s => s.CheckForUpdatesAsync(It.IsAny<string>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateCheckResult(true, "aaa", "bbb", "1 commit behind"));
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
