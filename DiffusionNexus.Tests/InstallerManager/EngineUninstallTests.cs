using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.UI.Services;
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

    private async Task<(InstallerManagerViewModel Vm, InstallerPackageCardViewModel Card)> CreateAsync(
        string? path = null, Mock<ICatalog>? catalog = null, Mock<IUnifiedLogger>? logger = null)
    {
        var vm = EngineTestHarness.CreateInstallerManagerViewModel(
            [Row(path)], dialogMock: _dialog, engine: _engine.Object, appSettingsMock: _settingsRepo,
            eventAggregatorMock: _events, onPackageRemoved: p => _removedRows.Add(p),
            catalogMock: catalog, loggerMock: logger);
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
