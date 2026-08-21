using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.Sync;
using DiffusionNexus.Tests.Helpers;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// The LoRA viewer no longer owns the metadata sync: the "Download Metadata" button and the
/// per-tile button both plan and execute against <see cref="ILibrarySyncService"/> (#521 WP2).
/// These tests pin the ViewModel's half of that contract — the scope and options it asks for,
/// the status text it shows, and the single tile rebuild after a run — with the service itself
/// mocked (its own behaviour is covered by <c>LibrarySyncServiceTests</c>).
/// <para>
/// No Avalonia platform is initialised: UI-thread marshalling goes through the injected
/// <see cref="ImmediateUiScheduler"/>, and the fresh-scope database reads go through an
/// injected <see cref="IServiceScopeFactory"/> instead of the <c>App.Services</c> locator.
/// </para>
/// </summary>
public class LoraViewerViewModelSyncTests
{
    private readonly Mock<ILibrarySyncService> _sync = new();
    private readonly Mock<IModelSyncService> _modelSync = new();
    private readonly Mock<IAppSettingsService> _settings = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IModelRepository> _models = new();
    private readonly Mock<ISyncStateRepository> _syncStates = new();

    /// <summary>Every value <c>SyncStatus</c> took during the run — the final one overwrites the progress text.</summary>
    private readonly List<string?> _statusHistory = [];

    private LoraViewerViewModel CreateViewModel(bool withSyncService = true)
    {
        _modelSync.Setup(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<InstalledModelFile>());
        _unitOfWork.SetupGet(u => u.Models).Returns(_models.Object);
        _unitOfWork.SetupGet(u => u.SyncStates).Returns(_syncStates.Object);

        var services = new ServiceCollection();
        services.AddSingleton(_modelSync.Object);
        services.AddSingleton(_unitOfWork.Object);
        var provider = services.BuildServiceProvider();

        var vm = new LoraViewerViewModel(
            _settings.Object,
            _modelSync.Object,
            civitaiClient: null,
            secureStorage: null,
            logger: null,
            baseModelCatalog: null,
            updateChecker: null,
            librarySync: withSyncService ? _sync.Object : null,
            uiScheduler: new ImmediateUiScheduler(),
            scopeFactory: provider.GetRequiredService<IServiceScopeFactory>());

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LoraViewerViewModel.SyncStatus)) _statusHistory.Add(vm.SyncStatus);
        };

        return vm;
    }

    private static SyncPlan PlanFor(SyncScope scope, params SyncPlanStep[] steps)
        => new(scope, SyncOptions.All, steps, DateTimeOffset.UtcNow);

    private static SyncPlanStep IdentifyStep(int count = 3)
        => new(SyncStepKind.IdentifyModel, count, TimeSpan.FromSeconds(3 * count), "Identify unknown files");

    private static SyncReport ReportFor(SyncPlan plan, int succeeded = 3, params SyncFailure[] failures)
        => new(
            plan,
            [new SyncStepReport(SyncStepKind.IdentifyModel, 3, 3, succeeded, 0, failures.Length)],
            failures,
            Cancelled: false,
            Elapsed: TimeSpan.FromSeconds(12),
            NewFilesDiscovered: 0);

    /// <summary>Plans return a one-step plan; executions return a report for the plan they were given.</summary>
    private SyncPlan SetupPlanAndExecute(params SyncFailure[] failures)
    {
        var plan = PlanFor(SyncScope.Library, IdentifyStep());

        _sync.Setup(s => s.PlanAsync(It.IsAny<SyncScope>(), It.IsAny<SyncOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(plan);
        _sync.Setup(s => s.ExecuteAsync(It.IsAny<SyncPlan>(), It.IsAny<IProgress<LibrarySyncProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncPlan p, IProgress<LibrarySyncProgress>? _, CancellationToken _) => ReportFor(p, failures: failures));

        return plan;
    }

    [Fact]
    public async Task DownloadMissingMetadata_PlansThenExecutesLibraryScope()
    {
        var vm = CreateViewModel();
        var plan = SetupPlanAndExecute();

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        _sync.Verify(s => s.PlanAsync(SyncScope.Library, It.IsAny<SyncOptions>(), It.IsAny<CancellationToken>()), Times.Once,
            "the bulk button syncs the whole library, not a folder or a model subset");
        _sync.Verify(s => s.ExecuteAsync(plan, It.IsAny<IProgress<LibrarySyncProgress>>(), It.IsAny<CancellationToken>()), Times.Once,
            "the run must execute the plan that was just presented, not re-plan");
    }

    /// <summary>
    /// The plan cannot know there is nothing to do: a plan that carries the discovery step always
    /// reports work, because nobody knows what a folder scan will find until it has run. So the
    /// "up to date" wording has to come from the <i>report</i> — discovered nothing, planned nothing.
    /// </summary>
    [Fact]
    public async Task DownloadMissingMetadata_UpToDateWordingWhenRunFoundNothing()
    {
        var vm = CreateViewModel();
        var plan = PlanFor(
            SyncScope.Library,
            new SyncPlanStep(SyncStepKind.DiscoverFiles, 0, TimeSpan.FromSeconds(2), "Discover new files in all LoRA sources"),
            new SyncPlanStep(SyncStepKind.IdentifyModel, 0, TimeSpan.Zero, "Identify unknown files"));

        _sync.Setup(s => s.PlanAsync(It.IsAny<SyncScope>(), It.IsAny<SyncOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(plan);
        _sync.Setup(s => s.ExecuteAsync(It.IsAny<SyncPlan>(), It.IsAny<IProgress<LibrarySyncProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncPlan p, IProgress<LibrarySyncProgress>? _, CancellationToken _) => new SyncReport(
                p,
                [
                    new SyncStepReport(SyncStepKind.DiscoverFiles, 0, 1, 1, 0, 0),
                    new SyncStepReport(SyncStepKind.IdentifyModel, 0, 0, 0, 0, 0),
                ],
                Failures: [],
                Cancelled: false,
                Elapsed: TimeSpan.FromSeconds(1),
                NewFilesDiscovered: 0));

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        vm.SyncStatus.Should().Be("Library is up to date — nothing to do",
            "\"Discovered 0\" is technically true and useless to read");
        _sync.Verify(s => s.ExecuteAsync(It.IsAny<SyncPlan>(), It.IsAny<IProgress<LibrarySyncProgress>>(), It.IsAny<CancellationToken>()), Times.Once,
            "the run still has to happen — the scan is the only thing that can prove there is nothing new");
        vm.IsBusy.Should().BeFalse("the busy overlay must be released on the nothing-to-do path too");
    }

    /// <summary>The `!plan.HasWork` early-out stays correct for an option set that excludes discovery.</summary>
    [Fact]
    public async Task DownloadMissingMetadata_EmptyPlanShortCircuitsWithoutExecuting()
    {
        var vm = CreateViewModel();
        _sync.Setup(s => s.PlanAsync(It.IsAny<SyncScope>(), It.IsAny<SyncOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlanFor(SyncScope.Library)); // no steps at all → HasWork is false

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        vm.SyncStatus.Should().Be("Library is up to date — nothing to do");
        _sync.Verify(s => s.ExecuteAsync(It.IsAny<SyncPlan>(), It.IsAny<IProgress<LibrarySyncProgress>>(), It.IsAny<CancellationToken>()), Times.Never,
            "a plan with no steps at all has nothing to run");
        vm.IsBusy.Should().BeFalse("the busy overlay must be released on the nothing-to-do path too");
    }

    [Fact]
    public async Task DownloadMissingMetadata_ProgressUpdatesStatus()
    {
        var vm = CreateViewModel();
        var plan = PlanFor(SyncScope.Library, IdentifyStep());

        IProgress<LibrarySyncProgress>? captured = null;
        _sync.Setup(s => s.PlanAsync(It.IsAny<SyncScope>(), It.IsAny<SyncOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(plan);
        _sync.Setup(s => s.ExecuteAsync(It.IsAny<SyncPlan>(), It.IsAny<IProgress<LibrarySyncProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncPlan p, IProgress<LibrarySyncProgress>? progress, CancellationToken _) =>
            {
                captured = progress;
                progress!.Report(new LibrarySyncProgress(SyncStepKind.FetchTags, 3, 68, "Foo"));
                return ReportFor(p);
            });

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        captured.Should().NotBeNull("the run must be given somewhere to report progress");
        _statusHistory.Should().Contain("Tags [3/68] Foo",
            "the status bar shows the step label, the position and the item currently being worked on");
    }

    [Fact]
    public async Task DownloadMissingMetadata_StatusIsReportSummary()
    {
        var vm = CreateViewModel();
        var plan = SetupPlanAndExecute();

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        vm.SyncStatus.Should().Be(ReportFor(plan).Summary,
            "the status bar shows the report's own summary rather than a second, divergent tally");
    }

    [Fact]
    public async Task DownloadMissingMetadata_StatusAppendsFailureCount()
    {
        var vm = CreateViewModel();
        var failures = new[]
        {
            new SyncFailure(SyncStepKind.IdentifyModel, 1, "a.safetensors", "timeout"),
            new SyncFailure(SyncStepKind.IdentifyModel, 2, "b.safetensors", "timeout"),
        };
        var plan = SetupPlanAndExecute(failures);

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        vm.SyncStatus.Should().Be(ReportFor(plan, failures: failures).Summary + " · 2 failed",
            "failures are the part of the outcome the user has to act on, so they are never silent");
    }

    /// <summary>
    /// I4. The first plan after the upgrade also derives a state row for every pre-existing model,
    /// which over a real library runs for seconds before any progress is reported — the user sees a
    /// frozen-looking app and no explanation. Said once, up front, and only when it is true.
    /// </summary>
    [Fact]
    public async Task DownloadMissingMetadata_AnnouncesTheFirstRunStateBackfill()
    {
        var vm = CreateViewModel();
        SetupPlanAndExecute();

        _models.Setup(m => m.CountAsync(It.IsAny<System.Linq.Expressions.Expression<Func<Model, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(2577);
        _syncStates.Setup(s => s.CountAsync(It.IsAny<System.Linq.Expressions.Expression<Func<ModelSyncState, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        _statusHistory.Should().Contain(s => s != null && s.StartsWith("Preparing sync state"));
    }

    /// <summary>…and stays quiet on every run after that, when there is nothing to backfill.</summary>
    [Fact]
    public async Task DownloadMissingMetadata_SaysNothingAboutBackfillWhenEveryModelHasAState()
    {
        var vm = CreateViewModel();
        SetupPlanAndExecute();

        _models.Setup(m => m.CountAsync(It.IsAny<System.Linq.Expressions.Expression<Func<Model, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(2577);
        _syncStates.Setup(s => s.CountAsync(It.IsAny<System.Linq.Expressions.Expression<Func<ModelSyncState, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(2577);

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        _statusHistory.Should().NotContain(s => s != null && s.StartsWith("Preparing sync state"));
    }

    [Fact]
    public async Task DownloadMissingMetadata_RebuildsTilesOnceAfterRun()
    {
        var vm = CreateViewModel();
        SetupPlanAndExecute();

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        _modelSync.Verify(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()), Times.Once,
            "the grid is rebuilt exactly once, after the run — not per phase as the old tile-driven sync did");
    }

    [Fact]
    public async Task DownloadMissingMetadata_WithoutServiceShowsUnavailable()
    {
        var vm = CreateViewModel(withSyncService: false);

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        vm.SyncStatus.Should().Be("Library sync not available.");
        _sync.Verify(s => s.PlanAsync(It.IsAny<SyncScope>(), It.IsAny<SyncOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DownloadMetadataForTile_UsesForModelsScopeWithForceIdentify()
    {
        var vm = CreateViewModel();
        var tile = CreateTile(modelId: 42);

        SyncScope? scope = null;
        SyncOptions? options = null;
        _sync.Setup(s => s.PlanAsync(It.IsAny<SyncScope>(), It.IsAny<SyncOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncScope s, SyncOptions o, CancellationToken _) =>
            {
                scope = s;
                options = o;
                return PlanFor(s, IdentifyStep(1));
            });
        _sync.Setup(s => s.ExecuteAsync(It.IsAny<SyncPlan>(), It.IsAny<IProgress<LibrarySyncProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncPlan p, IProgress<LibrarySyncProgress>? _, CancellationToken _) => ReportFor(p, succeeded: 1));
        _models.Setup(m => m.GetByIdWithIncludesAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync((Model?)null);

        var outcome = await vm.DownloadMetadataForTileAsync(tile);

        outcome.Applied.Should().BeTrue("a step that succeeded for the model means metadata was applied");
        scope!.Kind.Should().Be(SyncScopeKind.Models);
        scope.ModelIds.Should().Equal(42);
        options!.ForceIdentify.Should().BeTrue(
            "the per-tile button is an explicit re-fetch request, so a stored 'already checked' verdict must not skip it");
        options.Steps.Should().BeEquivalentTo(new[]
        {
            SyncStepKind.IdentifyModel, SyncStepKind.FetchTags, SyncStepKind.FetchImages,
        }, "one tile never triggers a library-wide discovery pass");
    }

    [Fact]
    public async Task DownloadMetadataForTile_WithNothingAppliedReturnsFalse()
    {
        var vm = CreateViewModel();
        var tile = CreateTile(modelId: 42);

        _sync.Setup(s => s.PlanAsync(It.IsAny<SyncScope>(), It.IsAny<SyncOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncScope s, SyncOptions _, CancellationToken _) => PlanFor(s, IdentifyStep(1)));
        _sync.Setup(s => s.ExecuteAsync(It.IsAny<SyncPlan>(), It.IsAny<IProgress<LibrarySyncProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncPlan p, IProgress<LibrarySyncProgress>? _, CancellationToken _) => ReportFor(p, succeeded: 0));

        var outcome = await vm.DownloadMetadataForTileAsync(tile);

        outcome.Applied.Should().BeFalse("nothing succeeded, so the detail view must say so instead of reloading unchanged data");
        outcome.IdentifyPlanned.Should().Be(3, "the step did run — the detail view may say Civitai has nothing");
    }

    /// <summary>
    /// C1. The detail view says "No metadata found on Civitai for this file." only when the
    /// identify step actually asked. A run that planned nothing asked nobody, so the outcome has
    /// to carry that fact out of the ViewModel — otherwise a selection bug reads as a verdict
    /// about Civitai, which is exactly how ~1,583 matched models came to be reported as unknown.
    /// </summary>
    [Fact]
    public async Task DownloadMetadataForTile_ReportsThatNothingWasPlannedWhenTheStepHadNoWork()
    {
        var vm = CreateViewModel();
        var tile = CreateTile(modelId: 42);

        _sync.Setup(s => s.PlanAsync(It.IsAny<SyncScope>(), It.IsAny<SyncOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncScope s, SyncOptions _, CancellationToken _) => PlanFor(s, IdentifyStep(0)));
        _sync.Setup(s => s.ExecuteAsync(It.IsAny<SyncPlan>(), It.IsAny<IProgress<LibrarySyncProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncPlan p, IProgress<LibrarySyncProgress>? _, CancellationToken _) => new SyncReport(
                p,
                [new SyncStepReport(SyncStepKind.IdentifyModel, 0, 0, 0, 0, 0)],
                [],
                Cancelled: false,
                Elapsed: TimeSpan.Zero,
                NewFilesDiscovered: 0));

        var outcome = await vm.DownloadMetadataForTileAsync(tile);

        outcome.Applied.Should().BeFalse();
        outcome.IdentifyPlanned.Should().Be(0, "nothing was asked, so nothing may be claimed about Civitai");
    }

    private static ModelTileViewModel CreateTile(int modelId)
    {
        var model = new Model { Id = modelId, Name = "Local Only LoRA", Type = ModelType.LORA };
        var version = new ModelVersion { Id = 700, Name = "v1.0", BaseModelRaw = "???", Model = model };
        version.Files.Add(new ModelFile { Id = 7000, FileName = "a.safetensors", IsPrimary = true, ModelVersion = version });
        model.Versions.Add(version);
        return ModelTileViewModel.FromModel(model);
    }
}
