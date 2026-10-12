using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.ConfigurationChecker;
using DiffusionNexus.UI.Services.ConfigurationChecker.Models;
using DiffusionNexus.UI.Services.Engine;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.InstallerManager;

public sealed class EngineUninstallTests : IDisposable
{
    private readonly string _root;
    private readonly Mock<IDialogService> _dialog = new();
    private readonly Mock<IManagedEngineControl> _engine = new();
    private readonly Mock<IAppSettingsRepository> _settingsRepo = new();
    private readonly Mock<IDatasetEventAggregator> _events = new();
    private readonly AppSettings _settings;
    private readonly List<InstallerPackage> _removedRows = [];

    public EngineUninstallTests()
    {
        _root = Directory.CreateTempSubdirectory("dn-engine-uninstall-").FullName;
        File.WriteAllText(Path.Combine(_root, "main.py"), "");
        Directory.CreateDirectory(Path.Combine(_root, "models"));

        _engine.Setup(e => e.TryBeginMaintenanceAsync()).ReturnsAsync(new Mock<IDisposable>().Object);
        _engine.Setup(e => e.StopIfIdleAsync()).ReturnsAsync(true);

        _settings = new AppSettings
        {
            Id = 1,
            ComfyUiServerMode = ComfyUiServerMode.Engine,
            BaseModelFolders =
            [
                new BaseModelFolder { Id = 1, FolderPath = Path.Combine(_root, "models"), InstallerPackageId = 7 },
                new BaseModelFolder { Id = 2, FolderPath = @"D:\Models", InstallerPackageId = 7 } // re-linked by an old backfill
            ],
            ImageGalleries = [new ImageGallery { Id = 3, FolderPath = Path.Combine(_root, "output") }],
            LoraSources = []
        };
        _settingsRepo.Setup(r => r.GetSettingsWithIncludesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_settings);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ } }

    private InstallerPackage Row(string? path = null) => new()
    {
        Id = 7, Name = "Diffusion Nexus Engine", InstallationPath = path ?? _root, Type = InstallerType.ComfyUI, IsAppManaged = true,
        ExecutablePath = string.Empty
    };

    private readonly InstallActivity _installActivity = new();

    private async Task<(InstallerManagerViewModel Vm, InstallerPackageCardViewModel Card)> CreateAsync(
        string? path = null, Mock<ICatalog>? catalog = null, Mock<IUnifiedLogger>? logger = null,
        Mock<IConfigurationCheckerService>? checker = null, IRemovalFileSystem? fileSystem = null)
    {
        var vm = EngineTestHarness.CreateInstallerManagerViewModel(
            [Row(path)], dialogMock: _dialog, engine: _engine.Object, appSettingsMock: _settingsRepo,
            eventAggregatorMock: _events, onPackageRemoved: p => _removedRows.Add(p),
            catalogMock: catalog, loggerMock: logger, installActivity: _installActivity,
            checkerMock: checker, removalFileSystem: fileSystem);
        vm.IsEngineTileVisible = true;
        await vm.LoadInstallationsCommand.ExecuteAsync(null);
        return (vm, vm.InstallerCards.Single(c => c.IsEngine));
    }

    private void Answer(int index) =>
        _dialog.Setup(d => d.ShowOptionsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>())).ReturnsAsync(index);

    [Fact]
    public async Task Confirmed_DeletesTheFolder_ForgetsTheEngine_AndSwitchesTheServer()
    {
        Answer(1); // "Uninstall"
        var (vm, card) = await CreateAsync();

        await vm.UninstallEngineAsync(card);

        Directory.Exists(_root).Should().BeFalse();
        _removedRows.Should().ContainSingle().Which.Id.Should().Be(7);
        _settingsRepo.Verify(r => r.RemoveBaseModelFolder(It.Is<BaseModelFolder>(f => f.Id == 1)), Times.Once);
        _settingsRepo.Verify(r => r.RemoveBaseModelFolder(It.Is<BaseModelFolder>(f => f.Id == 2)), Times.Never,
            "D:\\Models is the user's, whatever row it is linked to");
        _settingsRepo.Verify(r => r.RemoveImageGallery(It.Is<ImageGallery>(g => g.Id == 3)), Times.Once);
        _settings.ComfyUiServerMode.Should().Be(ComfyUiServerMode.CustomUrl);
        vm.InstallerCards.Single(c => c.IsEngine).ShowEngineInstallButton.Should().BeTrue();
        _events.Verify(e => e.PublishEngineChanged(It.IsAny<EngineChangedEventArgs>()), Times.Once);
        _events.Verify(e => e.PublishSettingsSaved(It.IsAny<SettingsSavedEventArgs>()), Times.Once);
    }

    [Fact]
    public async Task Cancelled_ChangesNothing()
    {
        Answer(0);
        var (vm, card) = await CreateAsync();

        await vm.UninstallEngineAsync(card);

        Directory.Exists(_root).Should().BeTrue();
        _removedRows.Should().BeEmpty();
        _engine.Verify(e => e.StopIfIdleAsync(), Times.Never);
    }

    [Fact]
    public async Task Busy_SaysWhy_AndAsksNothing()
    {
        _engine.SetupGet(e => e.HasActiveJobs).Returns(true);
        var (vm, card) = await CreateAsync();

        await vm.UninstallEngineAsync(card);

        _dialog.Verify(d => d.ShowMessageAsync(It.IsAny<string>(), It.Is<string>(m => m.Contains("a generation is running"))), Times.Once);
        _dialog.Verify(d => d.ShowOptionsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>()), Times.Never);
        Directory.Exists(_root).Should().BeTrue();
    }

    [Fact]
    public async Task MissingFolder_StillForgetsTheEngine()
    {
        Directory.Delete(_root, recursive: true);
        Answer(1);
        var (vm, card) = await CreateAsync();

        await vm.UninstallEngineAsync(card);

        _removedRows.Should().ContainSingle();
        _dialog.Verify(d => d.ShowOptionsAsync(It.IsAny<string>(), It.Is<string>(m => m.Contains("already gone")), It.IsAny<string[]>()), Times.Once);
    }

    [Fact]
    public async Task ServerOnCustomUrl_StaysThere()
    {
        _settings.ComfyUiServerMode = ComfyUiServerMode.CustomUrl;
        Answer(1);
        var (vm, card) = await CreateAsync();

        await vm.UninstallEngineAsync(card);

        _settings.ComfyUiServerMode.Should().Be(ComfyUiServerMode.CustomUrl);
    }

    // Review focus 3: a second click while the first uninstall waits on its dialog does nothing.
    [Fact]
    public async Task ASecondUninstall_WhileTheFirstRuns_DoesNothing()
    {
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dialog.Setup(d => d.ShowOptionsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>())).Returns(gate.Task);
        var (vm, card) = await CreateAsync();

        var first = vm.UninstallEngineAsync(card);
        // Bounded: without the guard the second call would wait on the same dialog forever.
        await vm.UninstallEngineAsync(card).WaitAsync(TimeSpan.FromSeconds(5));
        gate.SetResult(0);
        await first;

        _dialog.Verify(d => d.ShowOptionsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>()), Times.Once);
    }

    // Review focus 2 at the tile: a corrupted row naming a drive is never deleted, sized or swept by.
    [Fact]
    public async Task DriveRootRow_TouchesNothingOnDisk_ForgetsTheEngine_KeepsTheSettingsRows()
    {
        var drive = Path.GetPathRoot(_root)!; // holds _root, so a by-path sweep would match the settings rows
        Answer(1);
        var (vm, card) = await CreateAsync(drive);

        await vm.UninstallEngineAsync(card);

        AssertForgottenWithoutTouchingAnything(drive);
    }

    [Fact]
    public async Task RelativePathRow_TouchesNothingOnDisk_ForgetsTheEngine_KeepsTheSettingsRows()
    {
        Answer(1);
        var (vm, card) = await CreateAsync(@"Engine\ComfyUI");

        await vm.UninstallEngineAsync(card);

        AssertForgottenWithoutTouchingAnything(@"Engine\ComfyUI");
    }

    private void AssertForgottenWithoutTouchingAnything(string recordedFolder)
    {
        _dialog.Verify(d => d.ShowOptionsAsync(It.IsAny<string>(),
            It.Is<string>(m => m.Contains(recordedFolder) && m.Contains("not a folder the app can safely delete")),
            It.Is<string[]>(o => o.SequenceEqual(new[] { "Cancel", "Uninstall" }))), Times.Once);
        _engine.Verify(e => e.TryBeginMaintenanceAsync(), Times.Never, "the remover is never asked to delete anything");
        Directory.Exists(_root).Should().BeTrue();
        _removedRows.Should().ContainSingle().Which.Id.Should().Be(7);
        _settingsRepo.Verify(r => r.RemoveBaseModelFolder(It.IsAny<BaseModelFolder>()), Times.Never);
        _settingsRepo.Verify(r => r.RemoveImageGallery(It.IsAny<ImageGallery>()), Times.Never);
        _settingsRepo.Verify(r => r.RemoveLoraSource(It.IsAny<LoraSource>()), Times.Never);
        _settings.ComfyUiServerMode.Should().Be(ComfyUiServerMode.CustomUrl);
    }

    // Runtime checks fall through: a broken catalog or checker never blocks the uninstall.
    [Fact]
    public async Task FeatureCheckThrows_StillUninstallsTheEngineFolder()
    {
        var catalog = new Mock<ICatalog>();
        catalog.Setup(c => c.GetWorkloadAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("catalog is corrupt"));
        var logger = new Mock<IUnifiedLogger>();
        Answer(1);
        var (vm, card) = await CreateAsync(catalog: catalog, logger: logger);

        await vm.UninstallEngineAsync(card);

        Directory.Exists(_root).Should().BeFalse();
        _removedRows.Should().ContainSingle();
        logger.Verify(l => l.Warn(LogCategory.Installation, "Diffusion Nexus Engine",
            It.Is<string>(m => m.Contains("Could not check which models") && m.Contains("catalog is corrupt")),
            It.IsAny<string?>()), Times.Once);
        _dialog.Verify(d => d.ShowMessageAsync("Error", It.IsAny<string>()), Times.Never);
    }

    // I1: while the Engine folder is deleted the tile says so, offers no Features / Install / Uninstall, the
    // Features dialog refuses to open, and the uninstall holds an install-activity handle (without
    // refusing itself over it). Afterwards the handle is released.
    [Fact]
    public async Task WhileDeleting_TheTileShowsUninstalling_FeaturesRefuse_AndTheActivityIsHeld()
    {
        Answer(1);
        var hold = new TaskCompletionSource<IDisposable?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.Setup(e => e.TryBeginMaintenanceAsync()).Returns(hold.Task);
        var logger = new Mock<IUnifiedLogger>();
        var (vm, card) = await CreateAsync(logger: logger);
        var presented = false;
        vm.EngineFeaturesDialogPresenter = _ => { presented = true; return Task.CompletedTask; };

        var uninstall = vm.UninstallEngineAsync(card);
        await WaitUntilAsync(() => card.IsEngineUninstalling);

        card.VersionDisplay.Should().Be("Uninstalling...");
        card.ShowEngineStatusMessage.Should().BeTrue();
        card.EngineStatusMessage.Should().NotBeNullOrWhiteSpace();
        card.ShowEngineWorkloadsButton.Should().BeFalse();
        card.ShowEngineInstallButton.Should().BeFalse();
        card.ShowEngineUninstallMenuItem.Should().BeFalse();
        _installActivity.Running.Should().Be("Diffusion Nexus Engine uninstall");

        await vm.OpenEngineFeaturesAsync();
        presented.Should().BeFalse();
        _dialog.Verify(d => d.ShowMessageAsync("Diffusion Nexus Engine", It.Is<string>(m => m.Contains("being uninstalled"))), Times.Once);
        logger.Verify(l => l.Info(LogCategory.Installation, "Diffusion Nexus Engine",
            It.Is<string>(m => m.Contains("being uninstalled")), It.IsAny<string?>()), Times.Once);

        hold.SetResult(new Mock<IDisposable>().Object);
        await uninstall;

        Directory.Exists(_root).Should().BeFalse("the uninstall did not refuse itself over its own activity handle");
        _removedRows.Should().ContainSingle();
        _installActivity.Running.Should().BeNull();
    }

    // I1: a refused uninstall (the Engine started meanwhile) ends the uninstalling state and releases the handle.
    [Fact]
    public async Task RefusedWhileDeleting_EndsTheUninstallingState()
    {
        Answer(1);
        _engine.Setup(e => e.TryBeginMaintenanceAsync()).ReturnsAsync((IDisposable?)null);
        var (vm, card) = await CreateAsync();

        await vm.UninstallEngineAsync(card);

        card.IsEngineUninstalling.Should().BeFalse();
        card.ShowEngineWorkloadsButton.Should().BeTrue();
        card.ShowEngineUninstallMenuItem.Should().BeTrue();
        card.VersionDisplay.Should().Be("App-managed");
        _installActivity.Running.Should().BeNull();
        Directory.Exists(_root).Should().BeTrue();
    }

    // I1: an install that started while the question was open still refuses the uninstall.
    [Fact]
    public async Task AnInstallStartedWhileAsking_RefusesTheUninstall()
    {
        IDisposable? other = null;
        _dialog.Setup(d => d.ShowOptionsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>()))
            .ReturnsAsync(() => { other = _installActivity.Begin("Outpaint Vision"); return 1; });
        var (vm, card) = await CreateAsync();

        await vm.UninstallEngineAsync(card);
        other?.Dispose();

        Directory.Exists(_root).Should().BeTrue();
        _removedRows.Should().BeEmpty();
        _dialog.Verify(d => d.ShowMessageAsync(It.IsAny<string>(), It.Is<string>(m => m.Contains("Outpaint Vision is being installed"))), Times.Once);
        card.IsEngineUninstalling.Should().BeFalse();
    }

    // Minor: an Engine folder that is itself a junction loses only the link; the user is told the files
    // behind it stay, and where, and the console says so too.
    [Fact]
    public async Task AJunctionRoot_LosesTheLinkOnly_AndTheUserIsToldWhereTheFilesStay()
    {
        var temp = Directory.CreateTempSubdirectory("dn-engine-linked-root-").FullName;
        var target = Path.Combine(temp, "RealEngine");
        var link = Path.Combine(temp, "Engine");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "main.py"), "");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                { CreateNoWindow = true, UseShellExecute = false })!.WaitForExit();
            Directory.Exists(link).Should().BeTrue("the test needs the junction");
            var logger = new Mock<IUnifiedLogger>();
            Answer(1);
            var (vm, card) = await CreateAsync(link, logger: logger);

            await vm.UninstallEngineAsync(card);

            Directory.Exists(link).Should().BeFalse("the link is removed");
            File.Exists(Path.Combine(target, "main.py")).Should().BeTrue("the files behind the link stay");
            _removedRows.Should().ContainSingle();
            _dialog.Verify(d => d.ShowMessageAsync(EngineRemovalMessages.UninstallTitle,
                It.Is<string>(m => m.Contains("only the link was removed") && m.Contains(target))), Times.Once);
            logger.Verify(l => l.Info(LogCategory.Installation, "Diffusion Nexus Engine",
                It.Is<string>(m => m.Contains("only the link was removed") && m.Contains(target)), It.IsAny<string?>()), Times.Once);
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link); // the link only, before the tree
            Directory.Delete(temp, recursive: true);
        }
    }

    // An Engine folder that is itself a junction: the question says only the link goes and where the
    // files stay, without a size or a count of model files read through the link (none of them is deleted).
    [Fact]
    public async Task AJunctionRoot_TheQuestionSaysOnlyTheLinkGoes_WithoutSizeOrUnclaimedCount()
    {
        var temp = Directory.CreateTempSubdirectory("dn-engine-linked-ask-").FullName;
        var target = Path.Combine(temp, "RealEngine");
        var link = Path.Combine(temp, "Engine");
        Directory.CreateDirectory(Path.Combine(target, "models", "checkpoints"));
        File.WriteAllText(Path.Combine(target, "main.py"), "");
        File.WriteAllBytes(Path.Combine(target, "models", "checkpoints", "core-download.safetensors"), new byte[3 * 1024 * 1024]);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                { CreateNoWindow = true, UseShellExecute = false })!.WaitForExit();
            Directory.Exists(link).Should().BeTrue("the test needs the junction");
            Answer(0);
            var (vm, card) = await CreateAsync(link, catalog: CatalogWithout());

            await vm.UninstallEngineAsync(card);

            _dialog.Verify(d => d.ShowOptionsAsync(EngineRemovalMessages.UninstallTitle,
                It.Is<string>(m => m.Contains("only the link is removed") && m.Contains(target)
                                   && !m.Contains(" MB") && !m.Contains("no Engine feature uses")),
                It.IsAny<string[]>()), Times.Once);
            File.Exists(Path.Combine(target, "main.py")).Should().BeTrue("cancelled");
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link); // the link only, before the tree
            Directory.Delete(temp, recursive: true);
        }
    }

    // Minor: a model in the user's own folder that could not be deleted does not keep the Engine row.
    [Fact]
    public async Task AFailureOutsideTheEngineFolder_DoesNotKeepTheRow()
    {
        var library = Directory.CreateTempSubdirectory("dn-engine-user-lib-").FullName;
        try
        {
            var userModel = Path.Combine(library, "ae.safetensors");
            File.WriteAllText(userModel, "x");
            var catalog = new Mock<ICatalog>();
            catalog.Setup(c => c.GetWorkloadAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, CancellationToken _) => new InstallationConfiguration { Id = id, Name = id.ToString() });
            var checker = new Mock<IConfigurationCheckerService>();
            checker.Setup(c => c.CheckConfigurationAsync(It.IsAny<InstallationConfiguration>(), _root,
                    It.IsAny<ConfigurationCheckOptions?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ConfigurationCheckResult
                {
                    OverallStatus = ConfigurationStatus.Full, CustomNodesStatus = ConfigurationStatus.Full,
                    ModelsStatus = ConfigurationStatus.Full, InstallationType = ComfyUIInstallationType.Manual,
                    CustomNodeResults = [],
                    ModelResults = [new ModelCheckResult
                    {
                        Id = Guid.NewGuid(), Name = "ae", IsInstalled = true, SearchedPaths = [],
                        FoundAtPath = userModel, AllFoundPaths = [userModel]
                    }]
                });
            var fs = new Mock<IRemovalFileSystem>();
            fs.Setup(f => f.DeleteDirectory(_root)).Returns([]);
            fs.Setup(f => f.DeleteFile(userModel)).Throws(new IOException("in use by another process"));
            Answer(1); // "Delete these models too"
            var (vm, card) = await CreateAsync(catalog: catalog, checker: checker, fileSystem: fs.Object);

            await vm.UninstallEngineAsync(card);

            fs.Verify(f => f.DeleteFile(userModel), Times.Once);
            _removedRows.Should().ContainSingle().Which.Id.Should().Be(7);
            vm.InstallerCards.Single(c => c.IsEngine).ShowEngineInstallButton.Should().BeTrue();
            _dialog.Verify(d => d.ShowMessageAsync(EngineRemovalMessages.UninstallTitle,
                It.Is<string>(m => m.StartsWith("The Diffusion Nexus Engine was uninstalled.") && m.Contains(userModel) && m.Contains("in use"))), Times.Once);
        }
        finally { Directory.Delete(library, recursive: true); }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached");
            await Task.Delay(10);
        }
    }

    // C2(b): a model no feature declares (a Core download that landed in the Engine's models folder) is
    // named in the question: it goes with the Engine.
    /// <summary>A catalog that knows every Engine workload except <paramref name="missing"/>.</summary>
    private static Mock<ICatalog> CatalogWithout(params Guid[] missing)
    {
        var catalog = new Mock<ICatalog>();
        catalog.Setup(c => c.GetWorkloadAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) =>
                missing.Contains(id) ? null : new InstallationConfiguration { Id = id, Name = id.ToString() });
        return catalog;
    }

    [Fact]
    public async Task ModelsNoFeatureUses_AreNamedInTheQuestion()
    {
        var core = Path.Combine(_root, "models", "checkpoints", "core-download.safetensors");
        Directory.CreateDirectory(Path.GetDirectoryName(core)!);
        File.WriteAllBytes(core, new byte[2 * 1024 * 1024]);
        Answer(0);
        // Every feature's workload is known (the checker finds nothing of them), so the count is exact.
        var (vm, card) = await CreateAsync(catalog: CatalogWithout());

        await vm.UninstallEngineAsync(card);

        _dialog.Verify(d => d.ShowOptionsAsync(It.IsAny<string>(),
            It.Is<string>(m => m.Contains("It also holds 1 other model file (2 MB) that no Engine feature uses")),
            It.IsAny<string[]>()), Times.Once);
    }

    // A workload the catalog lacks has unknown files: counting "model files no feature uses" would
    // overcount them, so that line is left out; the uninstall itself is unchanged.
    [Fact]
    public async Task AWorkloadMissingFromTheCatalog_SaysNothingAboutModelsNoFeatureUses()
    {
        var core = Path.Combine(_root, "models", "checkpoints", "core-download.safetensors");
        Directory.CreateDirectory(Path.GetDirectoryName(core)!);
        File.WriteAllBytes(core, new byte[10]);
        var logger = new Mock<IUnifiedLogger>();
        Answer(1);
        var (vm, card) = await CreateAsync(catalog: CatalogWithout(EngineFeatureCatalog.Krea2Turbo), logger: logger);

        await vm.UninstallEngineAsync(card);

        _dialog.Verify(d => d.ShowOptionsAsync(It.IsAny<string>(),
            It.Is<string>(m => m.StartsWith("Uninstall the Diffusion Nexus Engine?") && !m.Contains("no Engine feature uses")),
            It.IsAny<string[]>()), Times.Once);
        logger.Verify(l => l.Warn(LogCategory.Installation, "Diffusion Nexus Engine",
            It.Is<string>(m => m.Contains("Canvas · Krea 2 Turbo") && m.Contains("catalog")), It.IsAny<string?>()), Times.Once);
        Directory.Exists(_root).Should().BeFalse("the uninstall still runs");
        _removedRows.Should().ContainSingle();
    }

    // C2(b): when the feature check fails, the app does not guess which models are unclaimed.
    [Fact]
    public async Task FeatureCheckThrows_SaysNothingAboutModelsNoFeatureUses()
    {
        var core = Path.Combine(_root, "models", "checkpoints", "core-download.safetensors");
        Directory.CreateDirectory(Path.GetDirectoryName(core)!);
        File.WriteAllBytes(core, new byte[10]);
        var catalog = new Mock<ICatalog>();
        catalog.Setup(c => c.GetWorkloadAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("catalog is corrupt"));
        Answer(0);
        var (vm, card) = await CreateAsync(catalog: catalog);

        await vm.UninstallEngineAsync(card);

        _dialog.Verify(d => d.ShowOptionsAsync(It.IsAny<string>(),
            It.Is<string>(m => m.StartsWith("Uninstall the Diffusion Nexus Engine?") && !m.Contains("no Engine feature uses")),
            It.IsAny<string[]>()), Times.Once);
    }

    [Fact]
    public async Task ALockedFileInTheEngine_KeepsTheRow_AndSaysWhy()
    {
        Answer(1);
        var (vm, card) = await CreateAsync();

        var locked = new FileStream(Path.Combine(_root, "main.py"), FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            await vm.UninstallEngineAsync(card);
        }
        finally
        {
            locked.Dispose();
        }

        _removedRows.Should().BeEmpty();
        _events.Verify(e => e.PublishEngineChanged(It.IsAny<EngineChangedEventArgs>()), Times.Once);
        _dialog.Verify(d => d.ShowMessageAsync(It.IsAny<string>(),
            It.Is<string>(m => m.Contains("not fully uninstalled") && m.Contains("main.py"))), Times.Once);
        _events.Verify(e => e.PublishSettingsSaved(It.IsAny<SettingsSavedEventArgs>()), Times.Never);
        vm.InstallerCards.Single(c => c.IsEngine).HasEngineRow.Should().BeTrue();
    }

    [Fact]
    public async Task ForgettingFails_AfterTheFolderWasDeleted_TheTileShowsItMissing()
    {
        Answer(1);
        var (vm, card) = await CreateAsync();
        card.IsEngineInstalled.Should().BeTrue();
        _settingsRepo.Setup(r => r.GetSettingsWithIncludesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        await vm.UninstallEngineAsync(card);

        Directory.Exists(_root).Should().BeFalse();
        card.IsEngineInstalled.Should().BeFalse();
        card.IsMissing.Should().BeTrue();
        _events.Verify(e => e.PublishEngineChanged(It.IsAny<EngineChangedEventArgs>()), Times.Once);
        _dialog.Verify(d => d.ShowMessageAsync("Error", It.Is<string>(m => m.Contains("database is locked"))), Times.Once);
    }
}
