using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
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
}
