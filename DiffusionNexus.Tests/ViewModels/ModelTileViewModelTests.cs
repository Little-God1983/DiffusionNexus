using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Installer.SDK.Shared.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// #438 regression + seam tests for <see cref="ModelTileViewModel"/>. The tile is now
/// constructed with an injected <see cref="ModelTileDependencies"/> bundle instead of
/// reaching into the <c>App.Services</c> static locator, so it can be exercised with
/// mocks/fakes.
/// <para>
/// The two historical production incidents this class used to guard directly are still
/// guarded, but the tile is no longer the place to do it — #521 Plan B moved both mechanisms
/// out of it. Socket exhaustion (a fresh <c>HttpClient</c> per download) is now answered by
/// the provider's typed client, pinned by
/// <c>LibrarySyncServiceTests.AddLibrarySync_ResolvesServiceWithStepsInOrder</c>; OOM / DB
/// bloat from oversized BLOBs is answered by the shared codec and the tile's self-heal,
/// pinned by <c>ModelTileThumbnailTests.OversizeSelfHeal_*</c>.
/// </para>
/// No Avalonia platform is initialised (which would deadlock the suite): the clipboard,
/// scheduler and dialog boundaries are all faked.
/// </summary>
public class ModelTileViewModelTests
{
    /// <summary>Records the text handed to the clipboard seam.</summary>
    private sealed class RecordingClipboard : IClipboardService
    {
        public List<string> Copied { get; } = [];

        public Task SetTextAsync(string text)
        {
            Copied.Add(text);
            return Task.CompletedTask;
        }
    }

    private static Model CreateLocalModel(string fileName, bool withCivitaiIds = false)
    {
        var model = new Model
        {
            Id = 7,
            Name = "Local Only LoRA",
            Type = ModelType.LORA,
            CivitaiId = withCivitaiIds ? 555 : null,
            CivitaiModelPageId = withCivitaiIds ? 555 : null,
        };

        var version = new ModelVersion
        {
            Id = 700,
            Name = "v1.0",
            BaseModelRaw = "Flux.1 D",
            CivitaiId = withCivitaiIds ? 5550 : null,
            Model = model,
        };
        version.Files.Add(new ModelFile { Id = 7000, FileName = fileName, IsPrimary = true, ModelVersion = version });
        model.Versions.Add(version);
        return model;
    }

    [Fact]
    public void FromModelWithADependencyBundleConstructsWithoutTheLocator()
    {
        var deps = new ModelTileDependencies(
            Logger: new Mock<IUnifiedLogger>().Object,
            Clipboard: new RecordingClipboard());

        var act = () => ModelTileViewModel.FromModel(CreateLocalModel("a.safetensors"), deps);

        act.Should().NotThrow("the tile must be constructible with an injected bundle, not App.Services");
    }

    [Fact]
    public void OpenOnCivitaiWithNoLinkWarnsThroughTheInjectedLogger()
    {
        // A local-only model (no Civitai id anywhere) has no page to open, so the command
        // logs a warning. That it reaches the *injected* logger proves the locator is gone.
        var logger = new Mock<IUnifiedLogger>();
        var tile = ModelTileViewModel.FromModel(
            CreateLocalModel("a.safetensors", withCivitaiIds: false),
            new ModelTileDependencies(Logger: logger.Object));

        tile.OpenOnCivitaiCommand.Execute(null);

        logger.Verify(
            l => l.Warn(It.IsAny<LogCategory>(), "OpenOnCivitai", It.IsAny<string>(), It.IsAny<string?>()),
            Times.Once());
    }

    [Fact]
    public async Task CopyFileNameRoutesThroughTheInjectedClipboard()
    {
        var clipboard = new RecordingClipboard();
        var tile = ModelTileViewModel.FromModel(
            CreateLocalModel("my_cool_lora.safetensors"),
            new ModelTileDependencies(Clipboard: clipboard));

        await tile.CopyFileNameCommand.ExecuteAsync(null);

        clipboard.Copied.Should().ContainSingle().Which.Should().Be("my_cool_lora");
    }

}
