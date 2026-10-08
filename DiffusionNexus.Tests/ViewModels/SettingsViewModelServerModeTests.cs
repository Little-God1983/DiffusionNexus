using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Tests.Helpers;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Engine;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.ViewModels;

public class SettingsViewModelServerModeTests
{
    private readonly Mock<IAppSettingsService> _settings = new();
    private readonly Mock<IEngineRootResolver> _root = new();
    private readonly Mock<IManagedComfyUiEngine> _engine = new();
    private readonly Mock<IDatasetEventAggregator> _events = new();

    private async Task<SettingsViewModel> LoadedAsync(ComfyUiServerMode mode, string? engineRoot = null,
        Action<SettingsViewModel>? beforeLoad = null)
    {
        _settings.Setup(s => s.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppSettings { Id = 1, ComfyUiServerMode = mode });
        _root.Setup(r => r.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(engineRoot);

        var vm = new SettingsViewModel(_settings.Object, new Mock<ISecureStorage>().Object,
            eventAggregator: _events.Object, engineRootResolver: _root.Object, engine: _engine.Object,
            looksInstalled: r => r is not null);
        beforeLoad?.Invoke(vm);
        await vm.LoadCommand.ExecuteAsync(null);
        return vm;
    }

    [Fact]
    public async Task Load_ReflectsTheStoredMode_AndDoesNotMarkChanges()
    {
        var vm = await LoadedAsync(ComfyUiServerMode.CustomUrl);

        vm.ComfyUiServerMode.Should().Be(ComfyUiServerMode.CustomUrl);
        vm.SelectedServerModeOption!.Mode.Should().Be(ComfyUiServerMode.CustomUrl);
        vm.IsCustomUrlMode.Should().BeTrue();
        vm.HasChanges.Should().BeFalse();
    }

    [Fact]
    public async Task PickingTheEngine_DisablesTheUrlRow_AndMarksChanges()
    {
        var vm = await LoadedAsync(ComfyUiServerMode.CustomUrl);

        vm.SelectedServerModeOption = vm.ServerModeOptions.Single(o => o.Mode == ComfyUiServerMode.Engine);

        vm.ComfyUiServerMode.Should().Be(ComfyUiServerMode.Engine);
        vm.IsCustomUrlMode.Should().BeFalse();
        vm.HasChanges.Should().BeTrue();
        vm.ServerModeOptions.Select(o => o.DisplayName).Should().Equal("Diffusion Nexus Engine", "Custom URL");
    }

    [Fact]
    public async Task TestConnection_IsOnlyAvailableForCustomUrl()
    {
        var vm = await LoadedAsync(ComfyUiServerMode.Engine);
        vm.TestComfyUiConnectionCommand.CanExecute(null).Should().BeFalse();

        vm.SelectedServerModeOption = vm.ServerModeOptions.Single(o => o.Mode == ComfyUiServerMode.CustomUrl);

        vm.TestComfyUiConnectionCommand.CanExecute(null).Should().BeTrue();
    }

    [Theory]
    [InlineData(ComfyUiServerMode.Engine, false)]
    [InlineData(ComfyUiServerMode.CustomUrl, true)]
    public async Task Load_PingsTheCustomUrl_OnlyInCustomUrlMode(ComfyUiServerMode mode, bool expectPing)
    {
        // The connection test sets IsTestingComfyUiConnection before its first await, so a ping
        // started by Load is seen synchronously.
        var pinged = false;
        await LoadedAsync(mode, beforeLoad: vm => vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.IsTestingComfyUiConnection) && vm.IsTestingComfyUiConnection)
                pinged = true;
        });

        pinged.Should().Be(expectPing);
    }

    [Theory]
    [InlineData(null, false, "Not installed — install it in the Installation Manager")]
    [InlineData(@"C:\Engine\ComfyUI", false, "Installed · not running (starts on first use)")]
    [InlineData(@"C:\Engine\ComfyUI", true, "Installed · running")]
    public async Task EngineStatus_DescribesInstallAndRunState(string? root, bool running, string expected)
    {
        _engine.SetupGet(e => e.BaseUrl).Returns(running ? "http://127.0.0.1:51234" : null);

        var vm = await LoadedAsync(ComfyUiServerMode.Engine, root);

        vm.EngineStatusText.Should().Be(expected);
    }

    private const string NotInstalledText = "Not installed — install it in the Installation Manager";

    /// <summary>Loads with the Engine not installed yet, then lets the resolver find a root.</summary>
    private async Task<(SettingsViewModel Vm, DatasetEventAggregator Events)> LoadedBeforeInstallAsync()
    {
        _settings.Setup(s => s.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppSettings { Id = 1, ComfyUiServerMode = ComfyUiServerMode.Engine });
        _root.Setup(r => r.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        var events = new DatasetEventAggregator();
        var vm = new SettingsViewModel(_settings.Object, new Mock<ISecureStorage>().Object,
            eventAggregator: events, uiScheduler: new ImmediateUiScheduler(),
            engineRootResolver: _root.Object, engine: _engine.Object, looksInstalled: r => r is not null);
        await vm.LoadCommand.ExecuteAsync(null);
        vm.EngineStatusText.Should().Be(NotInstalledText);
        _root.Setup(r => r.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(@"C:\Engine\ComfyUI");
        return (vm, events);
    }

    [Fact]
    public async Task EngineStatus_RefreshesWhenTheEngineIsPickedInTheDropdown()
    {
        var (vm, _) = await LoadedBeforeInstallAsync();
        vm.SelectedServerModeOption = vm.ServerModeOptions.Single(o => o.Mode == ComfyUiServerMode.CustomUrl);

        vm.SelectedServerModeOption = vm.ServerModeOptions.Single(o => o.Mode == ComfyUiServerMode.Engine);

        vm.EngineStatusText.Should().Be("Installed · not running (starts on first use)");
    }

    [Fact]
    public async Task EngineStatus_RefreshesOnAnExternalSettingsSaved()
    {
        var (vm, events) = await LoadedBeforeInstallAsync();

        events.PublishSettingsSaved(new SettingsSavedEventArgs());

        vm.EngineStatusText.Should().Be("Installed · not running (starts on first use)");
    }

    [Fact]
    public async Task EngineStatus_RefreshesOnEngineChanged()
    {
        var (vm, events) = await LoadedBeforeInstallAsync();

        events.PublishEngineChanged(new EngineChangedEventArgs());

        vm.EngineStatusText.Should().Be("Installed · not running (starts on first use)");
    }

    [Fact]
    public async Task EngineStatus_RefreshesWhenTheSettingsModuleIsShownAgain()
    {
        var (vm, _) = await LoadedBeforeInstallAsync();

        ((IModuleActivationAware)vm).OnModuleActivated();

        vm.EngineStatusText.Should().Be("Installed · not running (starts on first use)");
    }

    [Fact]
    public async Task EngineStatus_ResolverThrowing_DoesNotBreakLoad()
    {
        _settings.Setup(s => s.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppSettings { Id = 1 });
        _root.Setup(r => r.ResolveAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("boom"));
        var vm = new SettingsViewModel(_settings.Object, new Mock<ISecureStorage>().Object,
            engineRootResolver: _root.Object, engine: _engine.Object);

        await vm.LoadCommand.ExecuteAsync(null);

        vm.EngineStatusText.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenEngineFeatures_PublishesTheNavigation()
    {
        var vm = await LoadedAsync(ComfyUiServerMode.Engine);

        vm.OpenEngineFeaturesCommand.Execute(null);

        _events.Verify(e => e.PublishNavigateToEngineFeatures(It.IsAny<NavigateToEngineFeaturesEventArgs>()), Times.Once);
    }

    // #606 code review 2 (G4). Importing settings rewrites Server mode and URL in the database; an
    // open Inpaint/Outpaint panel only re-checks its readiness line on SettingsSaved.

    private const string ImportPath = @"C:\Exports\settings.json";

    private (SettingsViewModel Vm, Mock<ISettingsExportService> Export) ForImport(DatasetEventAggregator events)
    {
        _settings.Setup(s => s.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppSettings { Id = 1, ComfyUiServerMode = ComfyUiServerMode.CustomUrl });
        var export = new Mock<ISettingsExportService>();
        export.Setup(e => e.ReadAsync(ImportPath, It.IsAny<CancellationToken>())).ReturnsAsync(new SettingsExportData());
        var dialogs = new Mock<IDialogService>();
        dialogs.Setup(d => d.ShowOpenFileDialogAsync("Import Settings", "*.json")).ReturnsAsync(ImportPath);
        dialogs.Setup(d => d.ShowConfirmAsync("Import Settings", It.IsAny<string>())).ReturnsAsync(true);
        var vm = new SettingsViewModel(_settings.Object, new Mock<ISecureStorage>().Object,
            eventAggregator: events, exportService: export.Object, uiScheduler: new ImmediateUiScheduler())
        {
            DialogService = dialogs.Object
        };
        return (vm, export);
    }

    [Fact]
    public async Task Import_PublishesSettingsSavedOnce_WithoutReloadingItselfAgain()
    {
        var events = new DatasetEventAggregator();
        var published = 0;
        events.SettingsSaved += (_, _) => published++;
        var (vm, export) = ForImport(events);
        export.Setup(e => e.ImportAsync(ImportPath, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await vm.ImportSettingsCommand.ExecuteAsync(null);

        published.Should().Be(1, "open readiness lines re-check on SettingsSaved");
        _settings.Verify(s => s.GetSettingsAsync(It.IsAny<CancellationToken>()), Times.Once,
            "the import reloads once; its own SettingsSaved must not trigger a second reload");
        vm.StatusMessage.Should().Be("Settings imported from settings.json.");
    }

    [Fact]
    public async Task Import_ThatFails_DoesNotPublishSettingsSaved()
    {
        var events = new DatasetEventAggregator();
        var published = 0;
        events.SettingsSaved += (_, _) => published++;
        var (vm, export) = ForImport(events);
        export.Setup(e => e.ImportAsync(ImportPath, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidDataException("bad file"));

        await vm.ImportSettingsCommand.ExecuteAsync(null);

        published.Should().Be(0);
        vm.StatusMessage.Should().Be("Import failed: bad file");
    }

    // #606 code review 2 (G5). Load, module activation, the dropdown, SettingsSaved and
    // EngineChanged all refresh the Engine status; only the latest refresh may write it.

    [Fact]
    public async Task EngineStatus_OverlappingRefreshes_TheOlderFinishingLast_DoesNotOverwriteTheNewer()
    {
        var older = new TaskCompletionSource<string?>();
        var newer = new TaskCompletionSource<string?>();
        _root.SetupSequence(r => r.ResolveAsync(It.IsAny<CancellationToken>()))
            .Returns(older.Task)
            .Returns(newer.Task);
        var vm = new SettingsViewModel(_settings.Object, new Mock<ISecureStorage>().Object,
            engineRootResolver: _root.Object, engine: _engine.Object, looksInstalled: r => r is not null);

        var first = vm.RefreshEngineStatusAsync();
        var second = vm.RefreshEngineStatusAsync();

        newer.SetResult(@"C:\Engine\ComfyUI"); // installed meanwhile
        await second;
        older.SetResult(null);
        await first;

        vm.EngineStatusText.Should().Be("Installed · not running (starts on first use)");
    }

    [Fact]
    public async Task EngineStatus_AnOlderRefreshThatThrows_DoesNotClearTheNewerResult()
    {
        var older = new TaskCompletionSource<string?>();
        var newer = new TaskCompletionSource<string?>();
        _root.SetupSequence(r => r.ResolveAsync(It.IsAny<CancellationToken>()))
            .Returns(older.Task)
            .Returns(newer.Task);
        var vm = new SettingsViewModel(_settings.Object, new Mock<ISecureStorage>().Object,
            engineRootResolver: _root.Object, engine: _engine.Object, looksInstalled: r => r is not null);

        var first = vm.RefreshEngineStatusAsync();
        var second = vm.RefreshEngineStatusAsync();

        newer.SetResult(@"C:\Engine\ComfyUI");
        await second;
        older.SetException(new IOException("locked"));
        await first;

        vm.EngineStatusText.Should().Be("Installed · not running (starts on first use)");
    }

    // #606 code review 2 (G6). Every module's startup waits on the Settings load; the Engine-status
    // lookup must not hold it up.

    [Fact]
    public async Task Load_DoesNotWaitForTheEngineStatus()
    {
        var engineRoot = new TaskCompletionSource<string?>();
        _settings.Setup(s => s.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppSettings { Id = 1, ComfyUiServerMode = ComfyUiServerMode.Engine });
        _root.Setup(r => r.ResolveAsync(It.IsAny<CancellationToken>())).Returns(engineRoot.Task);
        var vm = new SettingsViewModel(_settings.Object, new Mock<ISecureStorage>().Object,
            engineRootResolver: _root.Object, engine: _engine.Object, looksInstalled: r => r is not null);

        var load = vm.LoadCommand.ExecuteAsync(null);

        load.IsCompleted.Should().BeTrue("the load must not wait on the Engine lookup");
        await load;
        vm.EngineStatusText.Should().BeEmpty();

        engineRoot.SetResult(@"C:\Engine\ComfyUI");
        await vm.EngineStatusRefresh;

        vm.EngineStatusText.Should().Be("Installed · not running (starts on first use)");
    }
}
