using System.Diagnostics;
using DiffusionNexus.Civitai;
using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.Sync;
using DiffusionNexus.Service.Services.Sync;
using DiffusionNexus.Service.Services.Sync.Steps;
using DiffusionNexus.Service.Services.Sync.Thumbnails;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DiffusionNexus.Tests.Sync.Service;

/// <summary>
/// Covers <see cref="LibrarySyncService"/> — the orchestrator that turns registered
/// <see cref="ISyncStep"/>s into a plan and then runs it under a single-flight gate (#521 WP2).
/// The steps themselves are faked here: what is under test is ordering, re-selection, tallying,
/// pacing, cancellation and the single-flight guard — not what any individual step does.
/// </summary>
public sealed class LibrarySyncServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly List<Model> _discovered = [];

    private const string ApiKey = "test-api-key";

    /// <summary>The one enabled LoRA source every seeded model file lives under.</summary>
    private const string SeedRoot = @"C:\m";

    public LibrarySyncServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDataAccessLayer(options => options.UseSqlite(_connection));

        var settings = new Mock<IAppSettingsService>();
        settings.Setup(s => s.GetCivitaiApiKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(ApiKey);
        // The real steps scope their selection to the enabled LoRA sources; the seeded files live here.
        settings.Setup(s => s.GetEnabledLoraSourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { SeedRoot });
        services.AddTransient(_ => settings.Object);

        // The real DiscoverFilesStep delegates the disk scan to this; the sync service reads the
        // resulting count back off the step for the report.
        var modelSync = new Mock<IModelSyncService>();
        modelSync.Setup(s => s.DiscoverNewFilesAsync(It.IsAny<IProgress<SyncProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _discovered);
        services.AddScoped(_ => modelSync.Object);

        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();
        context.Database.EnsureCreated();
    }

    private IServiceScopeFactory Scopes => _serviceProvider.GetRequiredService<IServiceScopeFactory>();

    /// <summary>The real initializer over the in-memory harness — Plan must run it before selecting.</summary>
    private SyncStateInitializer NewInitializer() => new(Scopes);

    /// <summary>
    /// Builds the service over the given fake steps. Nothing here sleeps: Civitai's courtesy
    /// interval is applied per request inside the steps (<c>ICivitaiRequestPacer</c>), and these
    /// steps are fakes that make no requests.
    /// </summary>
    private LibrarySyncService NewService(IEnumerable<ISyncStep> steps)
        => new(steps, NewInitializer(), Scopes, logger: null);

    private static SyncOptions OptionsFor(params SyncStepKind[] kinds) => new(new HashSet<SyncStepKind>(kinds));

    private static SyncItem Item(int id, string? name = null) => new(id, name ?? $"model-{id}", new object());

    // ---------------------------------------------------------------- Plan

    [Fact]
    public async Task Plan_IncludesOnlyRequestedSteps_AndCounts()
    {
        var discover = new FakeStep(SyncStepKind.DiscoverFiles, "Scan source folders for new files", TimeSpan.FromSeconds(2))
            .Selecting([Item(0, "scan")]);
        var identify = new FakeStep(SyncStepKind.IdentifyModel, "Identify models on Civitai", TimeSpan.FromSeconds(3))
            .Selecting([Item(1), Item(2), Item(3)]);
        var tags = new FakeStep(SyncStepKind.FetchTags, "Fetch tags", TimeSpan.FromSeconds(1.6))
            .Selecting([Item(1), Item(2)]);
        var images = new FakeStep(SyncStepKind.FetchImages, "Fetch image records", TimeSpan.FromSeconds(1.6))
            .Selecting([]);

        var service = NewService([discover, identify, tags, images]);

        var plan = await service.PlanAsync(
            SyncScope.Library,
            OptionsFor(SyncStepKind.DiscoverFiles, SyncStepKind.IdentifyModel, SyncStepKind.FetchTags));

        // Registration order is preserved, and the un-requested Images step is absent entirely.
        plan.Steps.Select(s => s.Kind).Should().Equal(
            SyncStepKind.DiscoverFiles, SyncStepKind.IdentifyModel, SyncStepKind.FetchTags);

        // Discover is a disk scan whose size is unknown until it runs, so it is always planned as 0.
        var discoverStep = plan.Steps.Single(s => s.Kind == SyncStepKind.DiscoverFiles);
        discoverStep.Count.Should().Be(0);
        // It is also not scope-aware: it scans every enabled source however the run was scoped.
        discoverStep.Description.Should().Be("Discover new files in all LoRA sources");

        plan.Steps.Single(s => s.Kind == SyncStepKind.IdentifyModel).Count.Should().Be(3);
        plan.Steps.Single(s => s.Kind == SyncStepKind.IdentifyModel).EstimatedDuration.Should().Be(TimeSpan.FromSeconds(9));
        plan.Steps.Single(s => s.Kind == SyncStepKind.FetchTags).Count.Should().Be(2);
        plan.Steps.Single(s => s.Kind == SyncStepKind.FetchTags).Description.Should().Be("Fetch tags");

        plan.Scope.Should().Be(SyncScope.Library);
        images.SelectCalls.Should().Be(0, "an un-requested step must not even be queried");
    }

    [Fact]
    public async Task Plan_SkipsKindsWithoutARegisteredStep()
    {
        var identify = new FakeStep(SyncStepKind.IdentifyModel).Selecting([Item(1)]);

        var service = NewService([identify]);

        // A kind this service has no step for — here because only the identify step was handed to
        // it — must be a no-op, not a crash: the option set is a wish, not a contract.
        var plan = await service.PlanAsync(
            SyncScope.Library,
            OptionsFor(SyncStepKind.IdentifyModel, SyncStepKind.Thumbnails));

        plan.Steps.Select(s => s.Kind).Should().Equal(SyncStepKind.IdentifyModel);
    }

    [Fact]
    public async Task Plan_WeightsImageEstimateByVersionCount()
    {
        // Two models, four versions between them: the images step calls Civitai once per version,
        // so the count is models but the estimate must be versions.
        var images = new FakeStep(SyncStepKind.FetchImages, "Fetch image records", TimeSpan.FromSeconds(1.6))
            .Selecting(
            [
                new SyncItem(1, "a", (IReadOnlyList<ImageCandidate>)[Candidate(1, 10), Candidate(1, 11), Candidate(1, 12)]),
                new SyncItem(2, "b", (IReadOnlyList<ImageCandidate>)[Candidate(2, 20)]),
            ]);

        var plan = await NewService([images]).PlanAsync(SyncScope.Library, OptionsFor(SyncStepKind.FetchImages));

        var step = plan.Steps.Single();
        step.Count.Should().Be(2);
        step.EstimatedDuration.Should().Be(TimeSpan.FromSeconds(1.6 * 4));

        static ImageCandidate Candidate(int modelId, int versionId) =>
            new(modelId, versionId, versionId + 1000, $"model-{modelId}", null);
    }

    /// <summary>
    /// Thumbnails deliberately has no arm of its own in <c>BuildPlanStep</c>: one item is one image
    /// is one request, so the default arm's "count items, scale by the per-item estimate" is already
    /// the truth. Pinned here because the images step next door is the exception, and copying its
    /// arm would double-count nothing while quietly breaking the estimate.
    /// </summary>
    [Fact]
    public async Task Plan_CountsThumbnailsOnePerImage()
    {
        var thumbnails = new FakeStep(SyncStepKind.Thumbnails, "Fetch thumbnails", TimeSpan.FromSeconds(0.4))
            .Selecting([Item(1, "a v1"), Item(1, "a v2"), Item(2, "b v1")]);

        var plan = await NewService([thumbnails]).PlanAsync(SyncScope.Library, OptionsFor(SyncStepKind.Thumbnails));

        var step = plan.Steps.Single();
        step.Count.Should().Be(3, "two versions of one model are two thumbnails, not one item");
        step.EstimatedDuration.Should().Be(TimeSpan.FromSeconds(1.2));
        step.Description.Should().Be("Fetch thumbnails");
    }

    [Fact]
    public async Task Plan_RunsInitializerFirst()
    {
        // A model that predates the sync-state table: the initializer must have given it a state
        // row before any step gets to select, or every step would see it as "never looked at".
        await SeedLegacyModelAsync("legacy");

        var step = new FakeStep(SyncStepKind.IdentifyModel).Selecting([]);
        step.OnSelect = async () =>
        {
            using var scope = _serviceProvider.CreateScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            return (await uow.SyncStates.GetModelIdsWithoutStateAsync()).Count;
        };

        await NewService([step]).PlanAsync(SyncScope.Library, OptionsFor(SyncStepKind.IdentifyModel));

        step.SelectCalls.Should().Be(1);
        step.ModelsWithoutStateAtSelect.Should().Be(0, "EnsureInitializedAsync runs before selection");
    }

    // ------------------------------------------------------------- Execute

    [Fact]
    public async Task Execute_ReportsPerStepCountsAndFailures()
    {
        var identify = new FakeStep(SyncStepKind.IdentifyModel)
            .Selecting([Item(1, "alpha"), Item(2, "beta"), Item(3, "gamma")])
            .Returning(item => item.ModelId switch
            {
                1 => SyncItemResult.Success,
                2 => SyncItemResult.Failure("HTTP 500"),
                _ => SyncItemResult.Skip,
            });
        var tags = new FakeStep(SyncStepKind.FetchTags)
            .Selecting([Item(4, "delta")])
            .Returning(_ => SyncItemResult.Success);

        var service = NewService([identify, tags]);
        var plan = await service.PlanAsync(SyncScope.Library, OptionsFor(SyncStepKind.IdentifyModel, SyncStepKind.FetchTags));

        var progress = new List<LibrarySyncProgress>();
        var report = await service.ExecuteAsync(plan, new Progress<LibrarySyncProgress>(progress.Add));

        report.Cancelled.Should().BeFalse();
        report.Steps.Select(s => s.Kind).Should().Equal(SyncStepKind.IdentifyModel, SyncStepKind.FetchTags);

        var identifyReport = report.Steps.Single(s => s.Kind == SyncStepKind.IdentifyModel);
        identifyReport.Planned.Should().Be(3);
        identifyReport.Processed.Should().Be(3);
        identifyReport.Succeeded.Should().Be(1);
        identifyReport.Skipped.Should().Be(1);
        identifyReport.Failed.Should().Be(1);

        report.Steps.Single(s => s.Kind == SyncStepKind.FetchTags).Succeeded.Should().Be(1);

        report.Failures.Should().ContainSingle();
        report.Failures[0].Should().BeEquivalentTo(new SyncFailure(SyncStepKind.IdentifyModel, 2, "beta", "HTTP 500"));

        // The API key is fetched once from settings and handed to every item.
        identify.ApiKeys.Should().AllBe(ApiKey);
        tags.ApiKeys.Should().AllBe(ApiKey);

        // Progress is 1-based and carries the item name for the status line.
        progress.Should().HaveCount(4);
        progress[0].Should().BeEquivalentTo(new LibrarySyncProgress(SyncStepKind.IdentifyModel, 1, 3, "alpha"));
        progress[2].Should().BeEquivalentTo(new LibrarySyncProgress(SyncStepKind.IdentifyModel, 3, 3, "gamma"));
        progress[3].Should().BeEquivalentTo(new LibrarySyncProgress(SyncStepKind.FetchTags, 1, 1, "delta"));

        report.Summary.Should().Contain("Identified 1/3");
    }

    [Fact]
    public async Task Execute_ReportsNewFilesDiscoveredFromTheDiscoverStep()
    {
        // Discover's item count is meaningless (one pseudo-item); the number that matters is the
        // one the scan itself produced, which only the step knows after it ran.
        for (var i = 0; i < 7; i++)
            _discovered.Add(new Model { Name = $"new-{i}", Type = ModelType.LORA, Source = DataSource.LocalFile });

        var discover = new DiscoverFilesStep(Scopes);

        var service = NewService([discover]);
        var plan = await service.PlanAsync(SyncScope.Library, OptionsFor(SyncStepKind.DiscoverFiles));

        var report = await service.ExecuteAsync(plan);

        report.NewFilesDiscovered.Should().Be(7);
        report.Summary.Should().StartWith("Discovered 7");
    }

    [Fact]
    public async Task Execute_ReSelectsItemsAtRunTime()
    {
        // The plan dialog may sit open for minutes; acting on the ids it captured would touch
        // models the user has since deleted.
        var identify = new FakeStep(SyncStepKind.IdentifyModel)
            .Selecting([Item(1), Item(2)])   // plan time
            .Selecting([Item(1)])            // run time
            .Returning(_ => SyncItemResult.Success);

        var service = NewService([identify]);
        var plan = await service.PlanAsync(SyncScope.Library, OptionsFor(SyncStepKind.IdentifyModel));

        var report = await service.ExecuteAsync(plan);

        identify.SelectCalls.Should().Be(2);
        identify.Executed.Select(i => i.ModelId).Should().Equal(1);

        var stepReport = report.Steps.Single();
        stepReport.Planned.Should().Be(2, "the plan is what the user agreed to");
        stepReport.Processed.Should().Be(1, "but only what still exists is touched");
        stepReport.Succeeded.Should().Be(1);
    }

    [Fact]
    public async Task Execute_CancellationMidStepReturnsPartialReportFlaggedCancelled()
    {
        using var cts = new CancellationTokenSource();

        var identify = new FakeStep(SyncStepKind.IdentifyModel)
            .Selecting([Item(1), Item(2), Item(3)]);
        identify.OnExecute = item =>
        {
            // The user hits Cancel while the first item is in flight.
            if (item.ModelId == 1) cts.Cancel();
            return Task.CompletedTask;
        };
        identify.Returning(_ => SyncItemResult.Success);

        var tags = new FakeStep(SyncStepKind.FetchTags).Selecting([Item(9)]).Returning(_ => SyncItemResult.Success);

        var service = NewService([identify, tags]);
        var plan = await service.PlanAsync(SyncScope.Library, OptionsFor(SyncStepKind.IdentifyModel, SyncStepKind.FetchTags));

        var report = await service.ExecuteAsync(plan, progress: null, cts.Token);

        report.Cancelled.Should().BeTrue();
        report.Summary.Should().Contain("(cancelled)");

        // What was done is still reported — a cancelled run is not a lost run.
        var identifyReport = report.Steps.Single(s => s.Kind == SyncStepKind.IdentifyModel);
        identifyReport.Processed.Should().Be(1);
        identifyReport.Succeeded.Should().Be(1);

        // Steps after the cancellation never start.
        report.Steps.Should().NotContain(s => s.Kind == SyncStepKind.FetchTags);
        tags.Executed.Should().BeEmpty();

        service.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task Execute_IsSingleFlight()
    {
        var gate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();

        var identify = new FakeStep(SyncStepKind.IdentifyModel).Selecting([Item(1)]).Returning(_ => SyncItemResult.Success);
        identify.OnExecute = async _ =>
        {
            entered.TrySetResult();
            await gate.Task;
        };

        var service = NewService([identify]);
        var plan = await service.PlanAsync(SyncScope.Library, OptionsFor(SyncStepKind.IdentifyModel));

        var first = service.ExecuteAsync(plan);
        await entered.Task;

        service.IsRunning.Should().BeTrue();

        // Its own type, not a bare InvalidOperationException: a step's SelectAsync raises that too
        // (GetRequiredService, Single() over nothing), and the viewer used to report every one of
        // them as "already running" at Info level. Still an InvalidOperationException underneath,
        // so any caller that predates the type keeps catching it.
        var second = () => service.ExecuteAsync(plan);
        await second.Should().ThrowAsync<SyncAlreadyRunningException>()
            .WithMessage("*already running*");
        (await Record.ExceptionAsync(second)).Should().BeAssignableTo<InvalidOperationException>();

        gate.SetResult();
        await first;

        service.IsRunning.Should().BeFalse();

        // ...and the gate really was released: a third run succeeds.
        await service.ExecuteAsync(plan);
    }

    /// <summary>
    /// R4. The orchestrator no longer paces at all — the courtesy interval moved to the request
    /// call sites — so a run over items that make no request must cost nothing but the work itself.
    /// </summary>
    [Fact]
    public async Task Execute_DoesNotPaceBetweenItems()
    {
        var identify = new FakeStep(SyncStepKind.IdentifyModel)
            .Selecting(Enumerable.Range(1, 10).Select(i => Item(i)).ToList())
            .Returning(_ => SyncItemResult.Success);

        var service = NewService([identify]);
        var plan = await service.PlanAsync(SyncScope.Library, OptionsFor(SyncStepKind.IdentifyModel));

        var stopwatch = Stopwatch.StartNew();
        var report = await service.ExecuteAsync(plan);
        stopwatch.Stop();

        report.Steps.Single().Succeeded.Should().Be(10);
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(200);
    }

    /// <summary>
    /// R5. A bug in one item used to take the whole run out: the exception escaped to the caller,
    /// so the tally of everything that HAD succeeded was thrown away and the user saw a raw
    /// NullReferenceException instead of a report. The bug is still reported — loudly, in the log
    /// and as its own counter on the report — but the other 2 499 models still get synced.
    /// </summary>
    [Fact]
    public async Task Execute_UnexpectedExceptionOnOneItemIsCountedAndRunContinues()
    {
        var identify = new FakeStep(SyncStepKind.IdentifyModel)
            .Selecting([Item(1), Item(2), Item(3)])
            .Returning(_ => SyncItemResult.Success);
        identify.OnExecute = item =>
        {
            if (item.ModelId == 2) throw new InvalidOperationException("boom");
            return Task.CompletedTask;
        };

        var service = NewService([identify]);
        var plan = await service.PlanAsync(SyncScope.Library, OptionsFor(SyncStepKind.IdentifyModel));

        var report = await service.ExecuteAsync(plan);

        var step = report.Steps.Single();
        step.Processed.Should().Be(3, "item 3 still ran");
        step.Succeeded.Should().Be(2);
        step.Failed.Should().Be(1);

        report.UnexpectedFailures.Should().Be(1);
        report.FirstUnexpectedError.Should().Contain("boom");
        report.Failures.Should().ContainSingle().Which.ModelId.Should().Be(2);

        service.IsRunning.Should().BeFalse();
    }

    /// <summary>
    /// R5, the other half: broadening the catch must not swallow a real cancellation. The user
    /// pressing Cancel is not an item fault, and the run has to unwind rather than mark every
    /// remaining model as "failed unexpectedly".
    /// </summary>
    [Fact]
    public async Task Execute_RealCancellationStillPropagates()
    {
        using var cts = new CancellationTokenSource();

        var identify = new FakeStep(SyncStepKind.IdentifyModel)
            .Selecting([Item(1), Item(2)])
            .Returning(_ => SyncItemResult.Success);
        identify.OnExecute = _ =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        var service = NewService([identify]);
        var plan = await service.PlanAsync(SyncScope.Library, OptionsFor(SyncStepKind.IdentifyModel));

        var report = await service.ExecuteAsync(plan, progress: null, cts.Token);

        report.Cancelled.Should().BeTrue();
        report.UnexpectedFailures.Should().Be(0, "a cancellation is not a bug in an item");
        report.Steps.Single().Failed.Should().Be(0);
    }

    /// <summary>
    /// R5. A <see cref="TaskCanceledException"/> whose token is not the run's is not a
    /// cancellation at all — it is HttpClient's own timeout, or a bug — and must be counted like
    /// any other unexpected failure rather than aborting the run as if the user had pressed Cancel.
    /// </summary>
    [Fact]
    public async Task Execute_ForeignTaskCanceledExceptionIsAnOrdinaryFailure()
    {
        using var unrelated = new CancellationTokenSource();
        await unrelated.CancelAsync();

        var identify = new FakeStep(SyncStepKind.IdentifyModel)
            .Selecting([Item(1), Item(2)])
            .Returning(_ => SyncItemResult.Success);
        identify.OnExecute = item =>
        {
            if (item.ModelId == 1) throw new TaskCanceledException("timeout", null, unrelated.Token);
            return Task.CompletedTask;
        };

        var service = NewService([identify]);
        var plan = await service.PlanAsync(SyncScope.Library, OptionsFor(SyncStepKind.IdentifyModel));

        var report = await service.ExecuteAsync(plan);

        report.Cancelled.Should().BeFalse();
        report.UnexpectedFailures.Should().Be(1);
        report.Steps.Single().Succeeded.Should().Be(1);
    }

    /// <summary>
    /// #535, R5 one level up. A step's <c>SelectAsync</c> runs outside the per-item try, so a
    /// non-cancellation throw there used to escape <c>ExecuteAsync</c> entirely — with the prior
    /// steps' per-item work already committed, the tally was discarded and no report was ever
    /// built. The user saw only the raw exception message; the record of what HAD been synced was
    /// gone. The escape is recorded on the report instead: the run aborts, but what it did is
    /// still stated, and the reason travels as <see cref="SyncReport.AbortReason"/>.
    /// </summary>
    [Fact]
    public async Task Execute_AThrowFromAStepsSelectionAbortsTheRunButKeepsTheReport()
    {
        var identify = new FakeStep(SyncStepKind.IdentifyModel)
            .Selecting([Item(1), Item(2)])
            .Returning(_ => SyncItemResult.Success);
        var tags = new FakeStep(SyncStepKind.FetchTags).Selecting([Item(9)]).Returning(_ => SyncItemResult.Success);
        // Selection #1 is plan time and must succeed; #2 is the run's re-selection, where the
        // database is suddenly locked. (#3 is the gate-release proof at the bottom.)
        tags.OnSelect = () => tags.SelectCalls == 2
            ? throw new InvalidOperationException("database is locked")
            : Task.FromResult(0);

        var service = NewService([identify, tags]);
        var plan = await service.PlanAsync(SyncScope.Library, OptionsFor(SyncStepKind.IdentifyModel, SyncStepKind.FetchTags));

        var report = await service.ExecuteAsync(plan);

        report.AbortReason.Should().Contain("database is locked",
            "the escape is a bug outside the item loop and must be stated as such, not laundered into an item failure");
        report.Cancelled.Should().BeFalse("nobody pressed Cancel");
        report.Summary.Should().Contain("(aborted)");

        var identifyReport = report.Steps.Single(s => s.Kind == SyncStepKind.IdentifyModel);
        identifyReport.Succeeded.Should().Be(2,
            "the two identified models are committed, and this report is the only record that they were");
        report.Steps.Should().NotContain(s => s.Kind == SyncStepKind.FetchTags,
            "the failing step never reached its item loop");

        service.IsRunning.Should().BeFalse();

        // ...and the gate really was released: the next run gets in.
        (await service.ExecuteAsync(plan)).AbortReason.Should().BeNull();
    }

    // ------------------------------------------------------ Thumbnail parallelism

    [Fact]
    public async Task Thumbnails_RunInParallel_UpToTheConfiguredConcurrency()
    {
        var probe = new ConcurrencyProbeStep(itemCount: 12);
        var service = NewService([probe]);
        var options = new SyncOptions(new HashSet<SyncStepKind> { SyncStepKind.Thumbnails }, ThumbnailConcurrency: 4);

        var plan = await service.PlanAsync(SyncScope.Library, options);
        var report = await service.ExecuteAsync(plan);

        probe.Executed.Should().Be(12);
        probe.MaxObservedConcurrency.Should().BeGreaterThan(1).And.BeLessThanOrEqualTo(4);
        report.Steps.Single().Succeeded.Should().Be(12);
    }

    [Fact]
    public async Task Thumbnails_ConcurrencyOne_StaysStrictlySequential()
    {
        var probe = new ConcurrencyProbeStep(itemCount: 6);
        var service = NewService([probe]);
        var options = new SyncOptions(new HashSet<SyncStepKind> { SyncStepKind.Thumbnails }, ThumbnailConcurrency: 1);

        var plan = await service.PlanAsync(SyncScope.Library, options);
        await service.ExecuteAsync(plan);

        probe.MaxObservedConcurrency.Should().Be(1);
    }

    [Fact]
    public async Task ParallelThumbnails_RecordEveryFailure_WithoutLosingAny()
    {
        var probe = new ConcurrencyProbeStep(itemCount: 10, failOddItems: true);
        var service = NewService([probe]);
        var options = new SyncOptions(new HashSet<SyncStepKind> { SyncStepKind.Thumbnails }, ThumbnailConcurrency: 4);

        var plan = await service.PlanAsync(SyncScope.Library, options);
        var report = await service.ExecuteAsync(plan);

        var step = report.Steps.Single();
        step.Processed.Should().Be(10);
        step.Failed.Should().Be(5);
        report.Failures.Should().HaveCount(5);
        report.Failures.Select(f => f.Reason).Should().AllBe("probe-failure");
    }

    /// <summary>
    /// The load-bearing half of the branch condition: a PACED API step must never parallelize,
    /// whatever the configured thumbnail concurrency says. Losing this silently turns the 1.5 s
    /// Civitai courtesy pacing into a burst and gets users rate-limited.
    /// </summary>
    [Fact]
    public async Task ANonThumbnailsStep_NeverParallelizes_RegardlessOfTheConcurrencySetting()
    {
        var probe = new ConcurrencyProbeStep(itemCount: 8, kind: SyncStepKind.FetchTags);
        var service = NewService([probe]);
        var options = new SyncOptions(new HashSet<SyncStepKind> { SyncStepKind.FetchTags }, ThumbnailConcurrency: 4);

        var plan = await service.PlanAsync(SyncScope.Library, options);
        await service.ExecuteAsync(plan);

        probe.Executed.Should().Be(8);
        probe.MaxObservedConcurrency.Should().Be(1);
    }

    /// <summary>The service, not the caller, owns the sane range: junk settings degrade, never throw.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(100)]
    public async Task ThumbnailConcurrency_OutOfRangeValues_AreClampedNotThrown(int configured)
    {
        var probe = new ConcurrencyProbeStep(itemCount: 10);
        var service = NewService([probe]);
        var options = new SyncOptions(new HashSet<SyncStepKind> { SyncStepKind.Thumbnails }, ThumbnailConcurrency: configured);

        var plan = await service.PlanAsync(SyncScope.Library, options);
        var report = await service.ExecuteAsync(plan);

        report.Steps.Single().Succeeded.Should().Be(10);
        probe.MaxObservedConcurrency.Should().BeLessThanOrEqualTo(8);
        if (configured < 1) probe.MaxObservedConcurrency.Should().Be(1, "a clamped-to-1 value must stay sequential");
    }

    // ------------------------------------------------------------------ DI

    [Fact]
    public void AddLibrarySync_ResolvesServiceWithStepsInOrder()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDataAccessLayer(options => options.UseSqlite(connection));
        services.AddSingleton(new Mock<ICivitaiClient>().Object);
        services.AddTransient(_ => new Mock<IAppSettingsService>().Object);
        services.AddLibrarySync();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ILibrarySyncService>().Should().BeOfType<LibrarySyncService>();
        provider.GetRequiredService<SyncStateInitializer>().Should().NotBeNull();
        provider.GetRequiredService<CivitaiMetadataApplier>().Should().NotBeNull();
        provider.GetRequiredService<SidecarMetadataApplier>().Should().NotBeNull();

        // IEnumerable<ISyncStep> preserves registration order, and the pipeline depends on it:
        // tags and images can only run on models identify has already matched, and thumbnails can
        // only be made for image records the images step has already fetched.
        provider.GetRequiredService<IEnumerable<ISyncStep>>().Select(s => s.Kind).Should().Equal(
            SyncStepKind.DiscoverFiles, SyncStepKind.IdentifyModel, SyncStepKind.FetchTags,
            SyncStepKind.FetchImages, SyncStepKind.Thumbnails);

        // The thumbnail step's provider is a typed client, so it only resolves if AddLibrarySync
        // registered the HttpClient for it too.
        provider.GetRequiredService<IThumbnailProvider>().Should().BeOfType<ThumbnailProvider>();

        // The gate is per-service, so the service itself must be a singleton.
        provider.GetRequiredService<ILibrarySyncService>().Should().BeSameAs(provider.GetRequiredService<ILibrarySyncService>());

        // R4: the pacer holds the timestamp of the last Civitai request, so two of them pace nothing.
        provider.GetRequiredService<ICivitaiRequestPacer>().Should().BeOfType<CivitaiRequestPacer>()
            .And.BeSameAs(provider.GetRequiredService<ICivitaiRequestPacer>());
    }

    // ------------------------------------------------------------- Fixtures

    private async Task SeedLegacyModelAsync(string name)
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var model = new Model { Name = name, Type = ModelType.LORA, Source = DataSource.LocalFile };
        var version = new ModelVersion { Name = "v1", BaseModelRaw = "???" };
        version.Files.Add(new ModelFile
        {
            FileName = name + ".safetensors",
            LocalPath = Path.Combine(SeedRoot, name + ".safetensors"),
            IsLocalFileValid = true,
            IsPrimary = true,
        });
        model.Versions.Add(version);

        await uow.Models.AddAsync(model);
        await uow.SaveChangesAsync();
    }

    /// <summary>
    /// A scripted <see cref="ISyncStep"/>: selections are dequeued per call (the last one repeats),
    /// results come from a per-item function, and every call is recorded for assertions.
    /// </summary>
    private sealed class FakeStep : ISyncStep
    {
        private readonly Queue<IReadOnlyList<SyncItem>> _selections = new();
        private Func<SyncItem, SyncItemResult> _result = _ => SyncItemResult.Success;

        public FakeStep(SyncStepKind kind, string description = "fake step", TimeSpan? perItem = null)
        {
            Kind = kind;
            Description = description;
            EstimatedPerItem = perItem ?? TimeSpan.FromSeconds(1);
        }

        public SyncStepKind Kind { get; }
        public string Description { get; }
        public TimeSpan EstimatedPerItem { get; }

        public int SelectCalls { get; private set; }
        public List<SyncItem> Executed { get; } = [];
        public List<string?> ApiKeys { get; } = [];
        public int? ModelsWithoutStateAtSelect { get; private set; }

        /// <summary>Runs at select time and records what it saw (used to prove ordering).</summary>
        public Func<Task<int>>? OnSelect { get; set; }

        /// <summary>Runs before the scripted result is produced; may throw or block.</summary>
        public Func<SyncItem, Task>? OnExecute { get; set; }

        public FakeStep Selecting(IReadOnlyList<SyncItem> items)
        {
            _selections.Enqueue(items);
            return this;
        }

        public FakeStep Returning(Func<SyncItem, SyncItemResult> result)
        {
            _result = result;
            return this;
        }

        public async Task<IReadOnlyList<SyncItem>> SelectAsync(SyncScope scope, SyncOptions options, DateTimeOffset now, CancellationToken ct)
        {
            SelectCalls++;
            if (OnSelect is not null) ModelsWithoutStateAtSelect = await OnSelect();

            if (_selections.Count == 0) return [];
            return _selections.Count > 1 ? _selections.Dequeue() : _selections.Peek();
        }

        public async Task<SyncItemResult> ExecuteOneAsync(SyncItem item, string? apiKey, CancellationToken ct)
        {
            Executed.Add(item);
            ApiKeys.Add(apiKey);

            // Deliberately no ThrowIfCancellationRequested: an item already in flight when Cancel is
            // pressed finishes (that is what the real steps do — they check on entry), so it is the
            // service's loop that must stop before the next one.
            if (OnExecute is not null) await OnExecute(item);

            return _result(item);
        }
    }

    /// <summary>
    /// A self-contained <c>Thumbnails</c> step that records the highest number of concurrently
    /// in-flight <see cref="ExecuteOneAsync"/> calls it observed, to prove (or disprove) that the
    /// service actually parallelizes this step's items.
    /// </summary>
    private sealed class ConcurrencyProbeStep : ISyncStep
    {
        private readonly IReadOnlyList<SyncItem> _items;
        private readonly bool _failOddItems;
        private int _inFlight;

        public int MaxObservedConcurrency;
        public int Executed;

        public ConcurrencyProbeStep(int itemCount, bool failOddItems = false, SyncStepKind kind = SyncStepKind.Thumbnails)
        {
            _items = Enumerable.Range(1, itemCount)
                .Select(i => new SyncItem(i, $"model-{i}", i))
                .ToList();
            _failOddItems = failOddItems;
            Kind = kind;
        }

        public SyncStepKind Kind { get; }
        public string Description => "probe";
        public TimeSpan EstimatedPerItem => TimeSpan.Zero;

        public Task<IReadOnlyList<SyncItem>> SelectAsync(SyncScope scope, SyncOptions options, DateTimeOffset now, CancellationToken ct)
            => Task.FromResult(_items);

        public async Task<SyncItemResult> ExecuteOneAsync(SyncItem item, string? apiKey, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            do
            {
                seen = MaxObservedConcurrency;
                if (now <= seen) break;
            } while (Interlocked.CompareExchange(ref MaxObservedConcurrency, now, seen) != seen);

            await Task.Delay(25, ct);

            Interlocked.Decrement(ref _inFlight);
            Interlocked.Increment(ref Executed);

            return _failOddItems && (int)item.Payload % 2 == 1
                ? SyncItemResult.Failure("probe-failure")
                : SyncItemResult.Success;
        }
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }
}
