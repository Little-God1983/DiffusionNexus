using System.Reflection;
using DiffusionNexus.Civitai;
using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Installer.SDK.Shared.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// Proves the #438 constructor-injection contract on <see cref="ModelDetailViewModel"/>:
/// the VM is fully constructible in a unit test with mocks/fakes (it no longer reaches
/// into the <c>App.Services</c> static locator), and the clipboard copy path routes
/// through the injected <see cref="IClipboardService"/> seam instead of a live Avalonia
/// <c>TopLevel</c>. No global Avalonia platform init is required (which would deadlock
/// the suite).
/// </summary>
public class ModelDetailViewModelTests
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

    private static ModelDetailViewModel CreateVm(
        IClipboardService? clipboard = null,
        IUiScheduler? scheduler = null,
        IServiceScopeFactory? scopeFactory = null)
        => new(
            civitaiClient: new Mock<ICivitaiClient>().Object,
            settingsService: new Mock<IAppSettingsService>().Object,
            secureStorage: new Mock<ISecureStorage>().Object,
            logger: new Mock<IUnifiedLogger>().Object,
            baseModelCatalog: null,
            scopeFactory: scopeFactory ?? new Mock<IServiceScopeFactory>().Object,
            dialogService: new Mock<IDialogService>().Object,
            clipboard: clipboard,
            uiScheduler: scheduler ?? new Helpers.ImmediateUiScheduler());

    [Fact]
    public void ConstructorWithMocksDoesNotThrowAndNeedsNoLocator()
    {
        var act = () => CreateVm();
        act.Should().NotThrow("the VM must be constructible with injected mocks, not App.Services");
    }

    [Fact]
    public async Task CopyTriggerWordsRoutesThroughTheInjectedClipboard()
    {
        var clipboard = new RecordingClipboard();
        var vm = CreateVm(clipboard);
        vm.TriggerWordsDisplay = "40fy, 3d style, fortnite";

        await vm.CopyTriggerWordsCommand.ExecuteAsync(null);

        clipboard.Copied.Should().ContainSingle().Which.Should().Be("40fy, 3d style, fortnite");
    }

    [Fact]
    public async Task CopyTriggerWordsWithNoTriggerWordsDoesNotTouchTheClipboard()
    {
        var clipboard = new RecordingClipboard();
        var vm = CreateVm(clipboard);
        vm.TriggerWordsDisplay = "   ";

        await vm.CopyTriggerWordsCommand.ExecuteAsync(null);

        clipboard.Copied.Should().BeEmpty();
    }

    [Fact]
    public void CivitaiThumbnailDownloadsShareASingleStaticHttpClient()
    {
        // #460: LoadCivitaiThumbnailAsync used to create a fresh `new HttpClient()` per
        // download — the same per-call-client anti-pattern that produced the tile's
        // documented socket-exhaustion incident (TIME_WAIT accumulation -> OOM after ~100
        // downloads). The tile's own client is gone (#521 Plan B moved its fetches onto
        // IThumbnailProvider's typed client), so this is the last one, and the rule still
        // holds for it: one shared, readonly static client instead of a per-call instance.
        var field = typeof(ModelDetailViewModel).GetField(
            "s_civitaiThumbnailClient", BindingFlags.NonPublic | BindingFlags.Static);

        field.Should().NotBeNull("Civitai thumbnail downloads must reuse one shared HttpClient");
        field!.IsInitOnly.Should().BeTrue("the shared client must be readonly so it can't be swapped per-call");
        field.FieldType.Should().Be<HttpClient>();
        field.GetValue(null).Should().NotBeNull();
    }

    /// <summary>
    /// The "Identity source:" row's label mapping (metadata-sync-overhaul Plan C, WP4 follow-up).
    /// Only the four outcomes that actually name a source get a label; the rest say nothing rather
    /// than something scary — <c>None</c> (never attempted), <c>NotIdentified</c> (every source was
    /// tried and none worked) and <c>Error</c> (the attempt itself failed) are not sources at all.
    /// </summary>
    [Theory]
    [InlineData(SyncOutcome.Matched, "Civitai")]
    [InlineData(SyncOutcome.Sidecar, "sidecar file")]
    [InlineData(SyncOutcome.Header, "file header")]
    [InlineData(SyncOutcome.Heuristic, "guessed from filename")]
    [InlineData(SyncOutcome.None, null)]
    [InlineData(SyncOutcome.NotIdentified, null)]
    [InlineData(SyncOutcome.Error, null)]
    public void DescribeIdentitySourceMapsEverySyncOutcome(SyncOutcome outcome, string? expected)
    {
        ModelDetailViewModel.DescribeIdentitySource(outcome).Should().Be(expected);
    }

    /// <summary>
    /// Review fix (Task 4 round 1): <c>LoadIdentitySourceAsync</c> is keyed to a specific model,
    /// unlike the model-invariant <c>LoadBaseModelCatalogAsync</c> it was modelled on. If the user
    /// switches tiles A→B before A's DB lookup returns, A's loader must not be able to complete
    /// after B's and overwrite B's correct chip with A's stale value. Drives both calls directly
    /// against a real <see cref="IServiceScopeFactory"/> (mocked repository underneath, same
    /// pattern as <c>LoraViewerViewModelSyncTests</c>) with model A's lookup gated behind a
    /// <see cref="TaskCompletionSource"/> so the test controls completion order deterministically
    /// instead of racing real timing.
    /// </summary>
    [Fact]
    public async Task LoadIdentitySourceAsyncDropsAStaleWriteFromAnOlderGeneration()
    {
        const int modelA = 101;
        const int modelB = 202;
        var stateA = new ModelSyncState { ModelId = modelA, MetadataOutcome = SyncOutcome.Matched };
        var stateB = new ModelSyncState { ModelId = modelB, MetadataOutcome = SyncOutcome.Sidecar };

        var slowLookupForA = new TaskCompletionSource<ModelSyncState?>();
        var syncStates = new Mock<ISyncStateRepository>();
        syncStates.Setup(s => s.GetByModelIdAsync(modelA, It.IsAny<CancellationToken>()))
            .Returns(slowLookupForA.Task);
        syncStates.Setup(s => s.GetByModelIdAsync(modelB, It.IsAny<CancellationToken>()))
            .ReturnsAsync(stateB);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.SyncStates).Returns(syncStates.Object);

        var services = new ServiceCollection();
        services.AddSingleton(unitOfWork.Object);
        var provider = services.BuildServiceProvider();

        var vm = CreateVm(scopeFactory: provider.GetRequiredService<IServiceScopeFactory>());
        var generationField = typeof(ModelDetailViewModel).GetField(
            "_identityLoadGeneration", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // Tile A's LoadAsync fires the slow lookup at generation 1 and does not await it (mirrors
        // the real fire-and-forget call site).
        generationField.SetValue(vm, 1);
        var taskA = vm.LoadIdentitySourceAsync(modelA, generation: 1);

        // The user switches to tile B before A resolves: generation advances to 2 and B's lookup
        // — which resolves immediately — completes and stamps the chip.
        generationField.SetValue(vm, 2);
        await vm.LoadIdentitySourceAsync(modelB, generation: 2);
        vm.IdentitySourceDisplay.Should().Be("sidecar file", "B's fast lookup is the current tile and must win");

        // A's slow lookup finally resolves. Its generation (1) no longer matches the VM's current
        // generation (2), so its write must be dropped rather than clobbering B's chip.
        slowLookupForA.SetResult(stateA);
        await taskA;
        vm.IdentitySourceDisplay.Should().Be("sidecar file", "a stale lookup for the previous tile must not overwrite the current tile's chip");
    }

    /// <summary>
    /// The local→Civitai file mapping behind <c>BuildLocalVersionTabs</c> must carry the hashes.
    /// A detail-panel "Download this version" of a LOCAL version hands the synthesised
    /// <see cref="CivitaiModelFile"/> to the one download path, where the SHA256 is what
    /// <c>DownloadCollisionPolicy</c> proves ownership of a colliding file with and what step 7
    /// verifies the transfer against. Dropping it made both blind: two local-only versions in one
    /// folder both resolved to <c>{stem}_0</c> and the second download replaced the first model's
    /// weights — the earlier filename-collision incident, reintroduced through a lossy DTO map.
    /// </summary>
    [Fact]
    public void LocalFileMappingCarriesTheHashesIntoTheDownloadRequest()
    {
        var file = new ModelFile
        {
            CivitaiId = 900,
            FileName = "V1.safetensors",
            SizeKB = 2048,
            IsPrimary = true,
            DownloadUrl = "https://civitai.test/api/download/models/1",
            HashSHA256 = "ABC123",
            HashAutoV2 = "AV2",
            HashCRC32 = "CRC",
            HashBLAKE3 = "B3",
        };

        var mapped = ModelDetailViewModel.ToCivitaiFile(file);

        mapped.Hashes.Should().NotBeNull();
        mapped.Hashes!.SHA256.Should().Be("ABC123",
            "the collision policy and the post-transfer verification both key off this hash");
        mapped.Hashes.AutoV2.Should().Be("AV2");
        mapped.Hashes.CRC32.Should().Be("CRC");
        mapped.Hashes.BLAKE3.Should().Be("B3");
        mapped.Id.Should().Be(900);
        mapped.Name.Should().Be("V1.safetensors");
        mapped.SizeKB.Should().Be(2048);
        mapped.Primary.Should().BeTrue();
        mapped.DownloadUrl.Should().Be("https://civitai.test/api/download/models/1");
    }
}
