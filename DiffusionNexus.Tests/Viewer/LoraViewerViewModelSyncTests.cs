using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.Sync;
using DiffusionNexus.Tests.Helpers;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// The LoRA viewer no longer owns the metadata sync: the "Download Metadata" button and the
/// per-tile button both plan and execute against <see cref="ILibrarySyncService"/> (#521 WP2).
/// Plan E turned the bulk button into a conversation — discover, plan, ask, run, stamp, report —
/// and these tests pin the ViewModel's half of it: the order of those calls, the options it asks
/// for, what the dialogs are handed, the status text, and the single tile rebuild after a run.
/// The service itself is mocked (its own behaviour is covered by <c>LibrarySyncServiceTests</c>).
/// <para>
/// No Avalonia platform is initialised: UI-thread marshalling goes through the injected
/// <see cref="ImmediateUiScheduler"/>, the fresh-scope database reads go through an injected
/// <see cref="IServiceScopeFactory"/> instead of the <c>App.Services</c> locator, and both dialogs
/// go through the <see cref="IDialogService"/> assigned to the ViewModel's inherited property.
/// </para>
/// </summary>
public class LoraViewerViewModelSyncTests
{
    private readonly Mock<ILibrarySyncService> _sync = new();
    private readonly Mock<IModelSyncService> _modelSync = new();
    private readonly Mock<IAppSettingsService> _settings = new();
    private readonly Mock<IDialogService> _dialogs = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IModelRepository> _models = new();
    private readonly Mock<ISyncStateRepository> _syncStates = new();

    /// <summary>
    /// The saved settings every run reads. Deliberately none of them the defaults, so an option
    /// carrying a default value cannot be mistaken for one that travelled from here.
    /// </summary>
    private readonly AppSettings _savedSettings = new()
    {
        SyncNotIdentifiedRetryDays = 14,
        SyncErrorRetryDays = 3,
        SyncThumbnailConcurrency = 6,
        LastLibrarySyncAt = new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero),
    };

    /// <summary>Every value <c>SyncStatus</c> took during the run — the final one overwrites the progress text.</summary>
    private readonly List<string?> _statusHistory = [];

    /// <summary>
    /// Ordered trace of what the flow did: <c>plan:discover</c>, <c>execute:discover</c>,
    /// <c>plan:run</c>, <c>plan-dialog</c>, <c>execute:run</c>, <c>report-dialog</c>. The order is
    /// the contract — discovery has to be finished before the dialog can show its counts.
    /// </summary>
    private readonly List<string> _calls = [];

    private readonly List<SyncOptions> _planned = [];
    private readonly List<SyncPlan> _executed = [];

    private SyncPlanDialogViewModel? _planDialogVm;
    private SyncReportDialogViewModel? _reportDialogVm;

    /// <summary>True once the plan dialog has been shown — the stale-plan case re-plans behind it.</summary>
    private bool _dialogShown;

    /// <summary>
    /// What the user does at the plan dialog. Default: start exactly what the dialog offers.
    /// Asynchronous because some answers involve the dialog first — ticking a Force and waiting for
    /// the re-plan it queues, which is the state the cancel wording has to be read from.
    /// </summary>
    private Func<SyncPlanDialogViewModel, Task<SyncPlanDialogResult>> _planDialogAnswer =
        vm => Task.FromResult(vm.BuildResult());

    private int _identifyCount = 3;

    /// <summary>Identify count for plans made after the dialog closed, or -1 to keep <see cref="_identifyCount"/>.</summary>
    private int _identifyCountAfterDialog = -1;

    /// <summary>
    /// Count given to every step other than identify. Zero by default — one step with work is all
    /// most of these tests need — but a run that has to cover all four kinds (the only kind of run
    /// allowed to stamp "last full sync") needs every row to have something in it.
    /// </summary>
    private int _otherStepCount;

    private int _executeCalls;

    /// <summary>1 = the discovery pre-run throws, 2 = the real run throws, 0 = neither.</summary>
    private int _throwOnExecuteCall;

    /// <summary>
    /// What that call throws. The gate's own refusal by default; a test that wants to prove a real
    /// bug is not laundered into "not now" swaps in a plain <see cref="InvalidOperationException"/>.
    /// </summary>
    private Exception _executeThrow = new SyncAlreadyRunningException();

    private LoraViewerViewModel CreateViewModel(bool withSyncService = true)
    {
        _modelSync.Setup(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<InstalledModelFile>());
        _unitOfWork.SetupGet(u => u.Models).Returns(_models.Object);
        _unitOfWork.SetupGet(u => u.SyncStates).Returns(_syncStates.Object);
        _settings.Setup(s => s.GetSettingsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_savedSettings);

        _dialogs.Setup(d => d.ShowSyncPlanDialogAsync(It.IsAny<SyncPlanDialogViewModel>()))
            .Returns((SyncPlanDialogViewModel dialogVm) =>
            {
                _calls.Add("plan-dialog");
                _planDialogVm = dialogVm;
                _dialogShown = true;
                return _planDialogAnswer(dialogVm);
            });
        _dialogs.Setup(d => d.ShowSyncReportDialogAsync(It.IsAny<SyncReportDialogViewModel>()))
            .Returns((SyncReportDialogViewModel dialogVm) =>
            {
                _calls.Add("report-dialog");
                _reportDialogVm = dialogVm;
                return Task.CompletedTask;
            });

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
            scopeFactory: provider.GetRequiredService<IServiceScopeFactory>())
        {
            DialogService = _dialogs.Object,
        };

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LoraViewerViewModel.SyncStatus)) _statusHistory.Add(vm.SyncStatus);
        };

        return vm;
    }

    private static SyncPlan PlanFor(SyncScope scope, SyncOptions options, params SyncPlanStep[] steps)
        => new(scope, options, steps, DateTimeOffset.UtcNow);

    private static SyncPlanStep IdentifyStep(int count = 3)
        => new(SyncStepKind.IdentifyModel, count, TimeSpan.FromSeconds(3 * count), "Identify unknown files");

    /// <summary>Discovery never has a count — it is a scan, and nobody can size it in advance.</summary>
    private static SyncPlanStep DiscoverStep()
        => new(SyncStepKind.DiscoverFiles, 0, TimeSpan.FromSeconds(2), "Discover new files in all LoRA sources");

    private static bool IsDiscovery(SyncOptions options) => options.Steps.Contains(SyncStepKind.DiscoverFiles);

    /// <summary>
    /// Wires the mocked service the way the real one answers this flow: a discovery request plans
    /// (and runs) the scan alone, every other request plans exactly the steps its options asked
    /// for, and a run succeeds at all of them minus the failures handed in.
    /// </summary>
    /// <param name="discoverFailures">
    /// What the scan could not read — an unreachable source folder, a row it could not write. The
    /// scan is its own run now, so these have to travel into the run's report or they vanish.
    /// </param>
    private void SetupSyncService(
        int discovered = 0,
        bool cancelled = false,
        SyncFailure[]? discoverFailures = null,
        int discoverUnexpected = 0,
        TimeSpan? discoverElapsed = null,
        TimeSpan? runElapsed = null,
        params SyncFailure[] failures)
    {
        _sync.Setup(s => s.PlanAsync(It.IsAny<SyncScope>(), It.IsAny<SyncOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncScope scope, SyncOptions options, CancellationToken _) =>
            {
                _planned.Add(options);
                _calls.Add(IsDiscovery(options) ? "plan:discover" : "plan:run");
                return PlanFor(scope, options, StepsFor(options));
            });

        _sync.Setup(s => s.ExecuteAsync(It.IsAny<SyncPlan>(), It.IsAny<IProgress<LibrarySyncProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncPlan plan, IProgress<LibrarySyncProgress>? progress, CancellationToken _) =>
            {
                _executeCalls++;
                if (_executeCalls == _throwOnExecuteCall)
                    throw _executeThrow;

                _executed.Add(plan);
                var isDiscovery = IsDiscovery(plan.Options);
                _calls.Add(isDiscovery ? "execute:discover" : "execute:run");

                return isDiscovery
                    ? ReportFor(plan, discovered, cancelled: false, discoverFailures ?? [], discoverElapsed,
                        discoverUnexpected, discoverUnexpected > 0 ? "scan: NullReferenceException" : null)
                    : ReportFor(plan, discovered: 0, cancelled, failures, runElapsed);
            });
    }


    /// <summary>The steps a plan for <paramref name="options"/> carries, with the counts the test asked for.</summary>
    private SyncPlanStep[] StepsFor(SyncOptions options)
    {
        if (IsDiscovery(options)) return [DiscoverStep()];

        var identify = _dialogShown && _identifyCountAfterDialog >= 0
            ? _identifyCountAfterDialog
            : _identifyCount;

        return options.Steps
            .OrderBy(k => (int)k)
            .Select(k => k == SyncStepKind.IdentifyModel
                ? IdentifyStep(identify)
                : new SyncPlanStep(k, _otherStepCount, TimeSpan.Zero, SyncReport.Label(k)))
            .ToArray();
    }

    /// <summary>A report for a plan: everything planned was processed, minus the failures on that step.</summary>
    private static SyncReport ReportFor(
        SyncPlan plan,
        int discovered,
        bool cancelled,
        IReadOnlyList<SyncFailure> failures,
        TimeSpan? elapsed = null,
        int unexpected = 0,
        string? firstUnexpectedError = null)
        => new(
            plan,
            plan.Steps.Select(s =>
            {
                var failed = failures.Count(f => f.Step == s.Kind);
                return new SyncStepReport(s.Kind, s.Count, s.Count, Math.Max(0, s.Count - failed), 0, failed);
            }).ToList(),
            failures,
            Cancelled: cancelled,
            Elapsed: elapsed ?? TimeSpan.FromSeconds(12),
            NewFilesDiscovered: IsDiscovery(plan.Options) ? discovered : 0,
            UnexpectedFailures: unexpected,
            FirstUnexpectedError: firstUnexpectedError);

    /// <summary>The report the run (not the discovery pre-run) produced, as the ViewModel saw it.</summary>
    private SyncReport RunReport() => ReportFor(_executed[^1], discovered: 0, cancelled: false, []);

    // ------------------------------------------------------------------ discover → plan → dialog

    /// <summary>
    /// Discovery runs to completion <i>before</i> the dialog opens, and is the only thing that pre-run
    /// does. That ordering is the whole reason the dialog can show honest counts: a file added since
    /// the app started is in the library — and in the identify count — by the time the user reads it,
    /// and the dialog needs no un-countable "Discover" row of its own.
    /// </summary>
    [Fact]
    public async Task DownloadMissingMetadata_DiscoversFirstThenAsks()
    {
        var vm = CreateViewModel();
        SetupSyncService(discovered: 7);

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        _calls.Should().ContainInOrder("plan:discover", "execute:discover", "plan:run", "plan-dialog", "execute:run");
        _executed[0].Options.Steps.Should().BeEquivalentTo(new[] { SyncStepKind.DiscoverFiles },
            "the pre-run scans and does nothing else — identifying is what the user is about to be asked about");

        _planned[1].Steps.Should().BeEquivalentTo(new[]
        {
            SyncStepKind.IdentifyModel, SyncStepKind.FetchTags, SyncStepKind.FetchImages, SyncStepKind.Thumbnails,
        }, "the dialog offers the four decidable steps; discovery has already happened");

        _planDialogVm!.Rows.Select(r => r.Kind).Should().Equal(
            SyncStepKind.IdentifyModel, SyncStepKind.FetchTags, SyncStepKind.FetchImages, SyncStepKind.Thumbnails);
        _planDialogVm.DiscoveredText.Should().Be("7 new files discovered",
            "the count comes from the discovery report the dialog was built after");
        _planDialogVm.LastRunText.Should().NotBe("Last full sync: never",
            "the saved LastLibrarySyncAt is what tells the user how stale the library is");

        _sync.Verify(s => s.PlanAsync(It.Is<SyncScope>(sc => sc.Kind != SyncScopeKind.Library), It.IsAny<SyncOptions>(), It.IsAny<CancellationToken>()),
            Times.Never, "the bulk button syncs the whole library, not a folder or a model subset");
    }

    /// <summary>
    /// The overlay is down while the dialog is up: a modal question behind a "Syncing..." spinner
    /// with a Cancel button is two different claims about the same moment.
    /// </summary>
    [Fact]
    public async Task DownloadMissingMetadata_DropsTheBusyOverlayWhileTheDialogIsOpen()
    {
        var vm = CreateViewModel();
        SetupSyncService();

        var busyDuringDialog = true;
        var cancellableDuringDialog = true;
        _planDialogAnswer = dialogVm =>
        {
            busyDuringDialog = vm.IsBusy;
            cancellableDuringDialog = vm.IsCancellable;
            return Task.FromResult(dialogVm.BuildResult());
        };

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        busyDuringDialog.Should().BeFalse("nothing is running while the user decides");
        cancellableDuringDialog.Should().BeFalse("there is nothing to cancel yet");
        vm.IsBusy.Should().BeFalse("and the overlay is down again when everything is finished");
    }

    [Fact]
    public async Task DownloadMissingMetadata_CancellingThePlanDialogRunsNothing()
    {
        var vm = CreateViewModel();
        SetupSyncService();
        _planDialogAnswer = _ => Task.FromResult(SyncPlanDialogResult.Cancelled());

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        vm.SyncStatus.Should().Be("Sync cancelled — nothing was run.",
            "the button did do something — it scanned — so silence would read as a dead click");
        _sync.Verify(s => s.ExecuteAsync(It.IsAny<SyncPlan>(), It.IsAny<IProgress<LibrarySyncProgress>>(), It.IsAny<CancellationToken>()),
            Times.Once, "only the discovery pre-run ran; the work itself was never started");
        _settings.Verify(s => s.UpdateLastLibrarySyncAtAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Never, "nothing was synced, so nothing may be stamped as synced");
        _reportDialogVm.Should().BeNull("there is no report to show for a run that never happened");
        vm.IsBusy.Should().BeFalse("the overlay must not come back for a run the user declined");
    }

    /// <summary>Closing an up-to-date dialog is not a cancellation of anything — say what is true instead.</summary>
    [Fact]
    public async Task DownloadMissingMetadata_ClosingAnUpToDateDialogSaysUpToDate()
    {
        var vm = CreateViewModel();
        _identifyCount = 0;
        SetupSyncService();
        _planDialogAnswer = _ => Task.FromResult(SyncPlanDialogResult.Cancelled());

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        vm.SyncStatus.Should().Be("Library is up to date — nothing to do");
        _planDialogVm!.IsUpToDate.Should().BeTrue("the dialog said so too — this is the same verdict, not a second one");
    }

    /// <summary>
    /// F3. The scan runs before the dialog and commits its new <c>Model</c> rows straight to the
    /// database, and nothing else refreshes the grid — <c>ILibraryChangeNotifier.ModelDownloaded</c>
    /// is raised only by the downloader, never by the discovery step. Only the run path rebuilt, so
    /// a user who dropped twelve LoRAs into a source folder, pressed the button, read
    /// "12 new files discovered" and then pressed Cancel got twelve rows in the database, none of
    /// them on screen until a manual Refresh — under a status line claiming nothing had happened.
    /// </summary>
    [Theory]
    [InlineData(12, "Sync cancelled — the scan added 12 new files.")]
    [InlineData(1, "Sync cancelled — the scan added 1 new file.")]
    public async Task DownloadMissingMetadata_CancellingTheDialogStillRebuildsAndSaysWhatTheScanAdded(
        int discovered, string expectedStatus)
    {
        var vm = CreateViewModel();
        SetupSyncService(discovered: discovered);
        _planDialogAnswer = _ => Task.FromResult(SyncPlanDialogResult.Cancelled());

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        _modelSync.Verify(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()), Times.Once,
            "the discovered rows are committed and stay invisible until the grid is re-projected from the database");
        vm.SyncStatus.Should().Be(expectedStatus,
            "\"nothing was run\" is false once the scan has added files — it is what the button just did");
    }

    /// <summary>The same rebuild is owed when the flow leaves through a refusal rather than a cancel.</summary>
    [Fact]
    public async Task DownloadMissingMetadata_RebuildsAfterTheScanEvenWhenTheRunIsRefused()
    {
        var vm = CreateViewModel();
        SetupSyncService(discovered: 5);
        _throwOnExecuteCall = 2;   // the run meets the single-flight gate; the scan already happened

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        _modelSync.Verify(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()), Times.Once,
            "the refusal is about the run, not about the five files the scan already committed");
    }

    /// <summary>And exactly once when the run path already did it — the finally is a backstop, not a second pass.</summary>
    [Fact]
    public async Task DownloadMissingMetadata_DoesNotRebuildTwiceWhenTheRunAlreadyDid()
    {
        var vm = CreateViewModel();
        SetupSyncService(discovered: 9);

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        _modelSync.Verify(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// F7. The plan behind the dialog is built before it opens; the Force expander re-plans live.
    /// An all-zero plan can therefore sit behind a dialog showing 40 thumbnails, and the cancel
    /// wording used to be read off the stale plan — telling the user the library was up to date
    /// one second after the dialog showed them work to do. The dialog's own current verdict wins.
    /// </summary>
    [Fact]
    public async Task DownloadMissingMetadata_CancellingAfterAForceReplanDoesNotClaimUpToDate()
    {
        var vm = CreateViewModel();
        _identifyCount = 0;                 // the plan the dialog opened on: nothing to do
        _identifyCountAfterDialog = 40;     // what ticking a Force re-plans into
        SetupSyncService();
        _planDialogAnswer = async dialogVm =>
        {
            dialogVm.ForceThumbnails = true;
            await dialogVm.WhenReplanSettles();
            return SyncPlanDialogResult.Cancelled();
        };

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        _planDialogVm!.IsUpToDate.Should().BeFalse("the re-plan found work and the dialog was showing it");
        vm.SyncStatus.Should().Be("Sync cancelled — nothing was run.",
            "the status may not contradict the numbers the user was looking at a second earlier");
    }

    /// <summary>
    /// F4. A source folder on a disconnected share makes <c>DiscoverNewFilesAsync</c> throw; the
    /// step records it as a failure precisely so a report can show it. Cancelling at the dialog
    /// used to leave that in the log alone, with a status line that said nothing went wrong.
    /// </summary>
    [Fact]
    public async Task DownloadMissingMetadata_CancellingAfterAFailedScanSaysTheScanFailed()
    {
        var vm = CreateViewModel();
        SetupSyncService(discoverFailures:
            [new SyncFailure(SyncStepKind.DiscoverFiles, 0, @"\\nas\loras", "network path not found")]);
        _planDialogAnswer = _ => Task.FromResult(SyncPlanDialogResult.Cancelled());

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        vm.SyncStatus.Should().Be("Sync cancelled — the scan reported 1 failure(s), see the log.",
            "the user concluded the library was fully scanned; only the log knew otherwise");
    }

    // ---------------------------------------------------------------------------- the run itself

    /// <summary>
    /// What the user ticked is what runs. The dialog may have been open for minutes, so the run
    /// re-plans with the chosen options rather than executing the plan the dialog was built from.
    /// </summary>
    [Fact]
    public async Task DownloadMissingMetadata_RunsTheOptionsTheDialogReturned()
    {
        var vm = CreateViewModel();
        SetupSyncService();
        _planDialogAnswer = _ => Task.FromResult(new SyncPlanDialogResult(true, _planned[^1] with
        {
            Steps = new HashSet<SyncStepKind> { SyncStepKind.FetchTags },
            ForceTags = true,
        }));

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        _planned.Should().HaveCount(3, "discovery, the plan behind the dialog, and the re-plan the run executes");
        _planned[^1].Steps.Should().BeEquivalentTo(new[] { SyncStepKind.FetchTags });
        _planned[^1].ForceTags.Should().BeTrue();

        _executed[^1].Options.Should().BeSameAs(_planned[^1],
            "the run executes the plan made from the user's choice, not the one behind the dialog");
    }

    /// <summary>
    /// The retry windows and the thumbnail fan-out are settings, not constants (Plan E Task 1), and
    /// they have to survive the round trip through the dialog — which lays the ticks and forces over
    /// the base options it was given rather than building its own.
    /// </summary>
    [Fact]
    public async Task DownloadMissingMetadata_CarriesTheSavedRetryWindowsAndConcurrencyIntoTheRun()
    {
        var vm = CreateViewModel();
        SetupSyncService();

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        foreach (var options in _planned)
        {
            options.Policy.NotIdentifiedRetryAfter.Should().Be(TimeSpan.FromDays(14), "SyncNotIdentifiedRetryDays");
            options.Policy.ErrorRetryAfter.Should().Be(TimeSpan.FromDays(3), "SyncErrorRetryDays");
        }

        _planned[1].ThumbnailConcurrency.Should().Be(6, "SyncThumbnailConcurrency, on the options the dialog builds from");
        _planned[^1].ThumbnailConcurrency.Should().Be(6, "and therefore on the ones the run executes");
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task DownloadMissingMetadata_StampsTheRunOnlyWhenItWasNotCancelled(bool cancelled, int stamps)
    {
        var vm = CreateViewModel();
        _otherStepCount = 2;   // every kind has work, so the dialog's default ticks cover all four
        SetupSyncService(cancelled: cancelled);

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        _settings.Verify(s => s.UpdateLastLibrarySyncAtAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Exactly(stamps),
            "\"last full sync\" is a claim about a run that finished — a cancelled one covered only part of the library");
    }

    /// <summary>
    /// F6. The dialog exists so the user can run a subset — and "Last full sync: …" is the only
    /// staleness signal the next dialog shows. A 20-second thumbnails-only top-up that stamped it
    /// made the viewer announce a full sync for metadata that had never been fetched at all.
    /// </summary>
    [Fact]
    public async Task DownloadMissingMetadata_DoesNotStampAFullSyncForASubsetRun()
    {
        var vm = CreateViewModel();
        SetupSyncService();   // only the identify row has work, so only it is ticked and run

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        _executed[^1].Options.Steps.Should().BeEquivalentTo(new[] { SyncStepKind.IdentifyModel },
            "this run covered one of the four offered kinds");
        _settings.Verify(s => s.UpdateLastLibrarySyncAtAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a run that skipped tags, images and thumbnails may not present itself as a full sync next week");
    }

    [Fact]
    public async Task DownloadMissingMetadata_ShowsTheReportOfTheRunItJustFinished()
    {
        var vm = CreateViewModel();
        SetupSyncService(discovered: 4, failures: [new SyncFailure(SyncStepKind.IdentifyModel, 1, "a.safetensors", "timeout")]);

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        _calls.Should().ContainInOrder("execute:run", "report-dialog");
        _reportDialogVm.Should().NotBeNull();
        _reportDialogVm!.SummaryText.Should().Be("Discovered 4 · Identified 2/3",
            "the report dialog projects the run's own report — three planned, one of them failed — " +
            "with the scan's count folded back in");
        _reportDialogVm.DiscoveredText.Should().Be("4 new files discovered",
            "the dialog's own discovered line says the same thing as its table");
        _reportDialogVm.HasFailures.Should().BeTrue("the failures are the part the user has to act on");
        vm.IsBusy.Should().BeFalse("the overlay comes down before the report, not behind it");
    }

    /// <summary>
    /// The stamp is a SQLite write at the peak of WAL contention — the run that just ended has been
    /// writing for minutes. Unguarded, its exception reached the outer catch and took the grid
    /// rebuild and the report dialog with it: everything the run achieved, lost to save a
    /// timestamp. A failed stamp may cost the timestamp and nothing else.
    /// </summary>
    [Fact]
    public async Task DownloadMissingMetadata_AFailedStampStillRebuildsAndReports()
    {
        var vm = CreateViewModel();
        _otherStepCount = 2;   // all four kinds run, so the stamp is actually attempted
        SetupSyncService();
        _settings.Setup(s => s.UpdateLastLibrarySyncAtAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        _modelSync.Verify(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()), Times.Once,
            "the models the run identified are already committed and stay invisible until the grid is rebuilt");
        _reportDialogVm.Should().NotBeNull("the user still gets the report of the run that did happen");
        vm.SyncStatus.Should().Be(RunReport().Summary,
            "and the status line is the run's tally, not an error message about a timestamp");
    }

    /// <summary>
    /// The scan is a separate run, so the run's own report counts none of it — and a report dialog
    /// whose table says "Discovered 0" a few lines above "9 new files discovered" is arguing with
    /// itself, as is a status bar that says the same. The scan's count is folded back into the
    /// report the moment the run returns, so every projection of it agrees.
    /// </summary>
    [Fact]
    public async Task DownloadMissingMetadata_FoldsTheScanCountIntoTheRunsOwnReport()
    {
        var vm = CreateViewModel();
        SetupSyncService(discovered: 9);

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        vm.SyncStatus.Should().StartWith("Discovered 9 ",
            "the status bar is DescribeOutcome's view of the very same report");
        _reportDialogVm!.SummaryText.Should().StartWith("Discovered 9 ");
        _reportDialogVm.DiscoveredText.Should().Be("9 new files discovered");
    }

    /// <summary>
    /// F4. …and so does everything else the scan produced. Its <c>Failures</c> were dropped on the
    /// floor with only the count surviving, so an unreadable source folder produced a report dialog
    /// that never mentioned the scan and a status line with no failure count on it.
    /// </summary>
    [Fact]
    public async Task DownloadMissingMetadata_FoldsTheScansFailuresIntoTheRunsReport()
    {
        var vm = CreateViewModel();
        var scanFailure = new SyncFailure(SyncStepKind.DiscoverFiles, 0, @"\\nas\loras", "network path not found");
        SetupSyncService(
            discoverFailures: [scanFailure],
            failures: new SyncFailure(SyncStepKind.IdentifyModel, 1, "a.safetensors", "timeout"));

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        var scanGroup = _reportDialogVm!.FailureGroups.Should()
            .ContainSingle(g => g.Kind == SyncStepKind.DiscoverFiles).Subject;
        scanGroup.Items.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Name = @"\\nas\loras", Reason = "network path not found" });

        _reportDialogVm.FailureGroups.Should().HaveCount(2, "the run's own failure is still there too");
        vm.SyncStatus.Should().EndWith("· 2 failed",
            "the scan's failure counts towards the tally the user is asked to act on");
    }

    /// <summary>A clean scan adds no group — the fold must not invent one.</summary>
    [Fact]
    public async Task DownloadMissingMetadata_ACleanScanAddsNoFailureGroup()
    {
        var vm = CreateViewModel();
        SetupSyncService(discovered: 4);

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        _reportDialogVm!.HasFailures.Should().BeFalse();
        _reportDialogVm.FailureGroups.Should().BeEmpty();
    }

    /// <summary>An item the scan lost to a bug no step claimed is still a bug when the scan is its own run.</summary>
    [Fact]
    public async Task DownloadMissingMetadata_FoldsTheScansUnexpectedFailuresIn()
    {
        var vm = CreateViewModel();
        SetupSyncService(discoverUnexpected: 1);

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        _reportDialogVm!.HasUnexpected.Should().BeTrue();
        vm.SyncStatus.Should().Contain("1 item failed unexpectedly (see log)");
    }

    /// <summary>
    /// F5. The scan is often the slowest part of the whole button press, and it is a separate run
    /// with its own stopwatch. Reporting only the second one told a user who waited four minutes
    /// that the work took 40 seconds.
    /// </summary>
    [Fact]
    public async Task DownloadMissingMetadata_TheReportedElapsedCoversTheScanToo()
    {
        var vm = CreateViewModel();
        SetupSyncService(
            discoverElapsed: TimeSpan.FromMinutes(3),
            runElapsed: TimeSpan.FromSeconds(40));

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        _reportDialogVm!.ElapsedText.Should().Be("3 min 40 s",
            "the press cost the scan plus the run, and the report says what it cost rather than estimating it");
    }

    /// <summary>
    /// The service admits one run at a time and throws on the second. A post-download completion
    /// sync can hold that slot for a moment, so both of this flow's runs can meet it — and an
    /// unhandled InvalidOperationException would surface as "Sync error: A library sync is
    /// already running." with a stack trace in the log for something that is not a bug.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DownloadMissingMetadata_ReportsARunAlreadyHoldingTheServiceInsteadOfThrowing(int throwOnCall)
    {
        var vm = CreateViewModel();
        SetupSyncService();
        _throwOnExecuteCall = throwOnCall;

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        vm.SyncStatus.Should().Be("A metadata sync is already running.");
        _reportDialogVm.Should().BeNull("there is no report — this run never got the service");
        vm.IsBusy.Should().BeFalse();
        vm.IsSyncRunning.Should().BeFalse("the refused run must not leave the buttons off");
    }

    /// <summary>
    /// F8. …and only that refusal. <c>InvalidOperationException</c> is what every step's
    /// <c>GetRequiredService&lt;IUnitOfWork&gt;()</c> and every <c>Single()</c> over an empty
    /// sequence raises too. Catching the base type told the user a DI regression was a busy
    /// service, logged it at Info without the exception, and re-enabled the button so the same
    /// wrong answer came back on every press. A genuine bug has to stay loud.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DownloadMissingMetadata_AnUnrelatedInvalidOperationIsReportedAsAnError(int throwOnCall)
    {
        var vm = CreateViewModel();
        SetupSyncService();
        _throwOnExecuteCall = throwOnCall;
        _executeThrow = new InvalidOperationException("No service for type 'IUnitOfWork' has been registered.");

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        vm.SyncStatus.Should().Be("Sync error: No service for type 'IUnitOfWork' has been registered.",
            "the generic catch logs at Error with the exception; the already-running path logs at Info without it");
        vm.IsBusy.Should().BeFalse();
        vm.IsSyncRunning.Should().BeFalse();
    }

    // ------------------------------------------------------------------- outcome, status, rebuild

    [Fact]
    public async Task DownloadMissingMetadata_ProgressUpdatesStatus()
    {
        var vm = CreateViewModel();
        IProgress<LibrarySyncProgress>? captured = null;

        _sync.Setup(s => s.PlanAsync(It.IsAny<SyncScope>(), It.IsAny<SyncOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncScope scope, SyncOptions options, CancellationToken _) =>
            {
                _planned.Add(options);
                return PlanFor(scope, options, StepsFor(options));
            });
        _sync.Setup(s => s.ExecuteAsync(It.IsAny<SyncPlan>(), It.IsAny<IProgress<LibrarySyncProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncPlan plan, IProgress<LibrarySyncProgress>? progress, CancellationToken _) =>
            {
                _executed.Add(plan);
                if (progress is not null)
                {
                    captured = progress;
                    progress.Report(new LibrarySyncProgress(SyncStepKind.FetchTags, 3, 68, "Foo"));
                }

                return ReportFor(plan, 0, false, []);
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
        SetupSyncService();

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        vm.SyncStatus.Should().Be(RunReport().Summary,
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
        SetupSyncService(failures: failures);

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        vm.SyncStatus.Should().Be(
            ReportFor(_executed[^1], discovered: 0, cancelled: false, failures).Summary + " · 2 failed",
            "failures are the part of the outcome the user has to act on, so they are never silent");
    }

    /// <summary>
    /// The plan behind the dialog can go stale — the dialog is modal, and the user can leave it open
    /// while a per-tile fetch or a download's completion sync does the same work. The re-plan then
    /// finds nothing, and the honest verdict for that run is the report's, not the dialog's.
    /// </summary>
    [Fact]
    public async Task DownloadMissingMetadata_UpToDateWordingWhenTheRunFoundNothingLeft()
    {
        var vm = CreateViewModel();
        _identifyCountAfterDialog = 0;
        SetupSyncService();

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        vm.SyncStatus.Should().Be("Library is up to date — nothing to do",
            "\"Discovered 0\" is technically true and useless to read");
        vm.IsBusy.Should().BeFalse("the busy overlay must be released on the nothing-to-do path too");
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
        SetupSyncService();

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
        SetupSyncService();

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
        SetupSyncService();

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

    /// <summary>Without a dialog service there is nobody to ask, so the run stops before it starts.</summary>
    [Fact]
    public async Task DownloadMissingMetadata_WithoutADialogServiceDoesNotRunUnasked()
    {
        var vm = CreateViewModel();
        SetupSyncService();
        vm.DialogService = null;

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        vm.SyncStatus.Should().Be("Dialog service not available.");
        _sync.Verify(s => s.ExecuteAsync(It.IsAny<SyncPlan>(), It.IsAny<IProgress<LibrarySyncProgress>>(), It.IsAny<CancellationToken>()),
            Times.Once, "the discovery pre-run had already happened; nothing beyond it may run unasked");
    }

    // ------------------------------------------------------------------------------ the per-tile button

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
                return PlanFor(s, o, IdentifyStep(1));
            });
        _sync.Setup(s => s.ExecuteAsync(It.IsAny<SyncPlan>(), It.IsAny<IProgress<LibrarySyncProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncPlan p, IProgress<LibrarySyncProgress>? _, CancellationToken _) =>
                new SyncReport(p, [new SyncStepReport(SyncStepKind.IdentifyModel, 3, 3, 1, 0, 0)], [], false, TimeSpan.FromSeconds(2), 0));
        _models.Setup(m => m.GetByIdWithIncludesAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync((Model?)null);

        var outcome = await vm.DownloadMetadataForTileAsync(tile);

        outcome.Applied.Should().BeTrue("a step that succeeded for the model means metadata was applied");
        scope!.Kind.Should().Be(SyncScopeKind.Models);
        scope.ModelIds.Should().Equal(42);
        options!.ForceIdentify.Should().BeTrue(
            "the per-tile button is an explicit re-fetch request, so a stored 'already checked' verdict must not skip it");
        options.ForceThumbnails.Should().BeTrue(
            "same request, same reasoning: a stored failure verdict must not make the button do nothing. " +
            "Selection still skips images that already have bytes, so forcing only retries failures");
        options.Steps.Should().BeEquivalentTo(new[]
        {
            SyncStepKind.IdentifyModel, SyncStepKind.FetchTags, SyncStepKind.FetchImages,
            SyncStepKind.Thumbnails,
        }, "one tile never triggers a library-wide discovery pass, but the thumbnail is half of what the user pressed the button for");
    }

    /// <summary>
    /// The forces cover identify and thumbnails, not the un-forced tags/images fetches — those are
    /// still judged against a retry window, and that window is the user's, not the built-in default.
    /// </summary>
    [Fact]
    public async Task DownloadMetadataForTile_UsesTheSavedRetryWindows()
    {
        var vm = CreateViewModel();
        await vm.ScrollRetryPolicyLoad; // the startup read the per-tile fetch shares with the tiles

        SyncOptions? options = null;
        _sync.Setup(s => s.PlanAsync(It.IsAny<SyncScope>(), It.IsAny<SyncOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncScope s, SyncOptions o, CancellationToken _) =>
            {
                options = o;
                return PlanFor(s, o, IdentifyStep(1));
            });
        _sync.Setup(s => s.ExecuteAsync(It.IsAny<SyncPlan>(), It.IsAny<IProgress<LibrarySyncProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncPlan p, IProgress<LibrarySyncProgress>? _, CancellationToken _) =>
                new SyncReport(p, [new SyncStepReport(SyncStepKind.IdentifyModel, 1, 1, 0, 0, 0)], [], false, TimeSpan.Zero, 0));

        await vm.DownloadMetadataForTileAsync(CreateTile(modelId: 42));

        options!.Policy.NotIdentifiedRetryAfter.Should().Be(TimeSpan.FromDays(14));
        options.Policy.ErrorRetryAfter.Should().Be(TimeSpan.FromDays(3));
    }

    [Fact]
    public async Task DownloadMetadataForTile_WithNothingAppliedReturnsFalse()
    {
        var vm = CreateViewModel();
        var tile = CreateTile(modelId: 42);

        _sync.Setup(s => s.PlanAsync(It.IsAny<SyncScope>(), It.IsAny<SyncOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncScope s, SyncOptions o, CancellationToken _) => PlanFor(s, o, IdentifyStep(1)));
        _sync.Setup(s => s.ExecuteAsync(It.IsAny<SyncPlan>(), It.IsAny<IProgress<LibrarySyncProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncPlan p, IProgress<LibrarySyncProgress>? _, CancellationToken _) =>
                new SyncReport(p, [new SyncStepReport(SyncStepKind.IdentifyModel, 3, 3, 0, 0, 0)], [], false, TimeSpan.FromSeconds(2), 0));

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
            .ReturnsAsync((SyncScope s, SyncOptions o, CancellationToken _) => PlanFor(s, o, IdentifyStep(0)));
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

    // -------------------------------------------------------------------------------- single flight

    /// <summary>
    /// R10. The sync service admits one run at a time and throws on the second. The old code
    /// assigned <c>_metadataSyncCts</c> before it looked at anything, so a second press both
    /// started a doomed run and stranded the first run's token — Cancel then cancelled a token
    /// nobody was listening to, and the run the user wanted stopped kept going to the end.
    /// </summary>
    [Fact]
    public async Task DownloadMissingMetadata_SecondCallWhileRunningIsRefusedAndCancelStillReachesTheFirstRun()
    {
        var vm = CreateViewModel();

        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        CancellationToken firstRunToken = default;

        _sync.Setup(s => s.PlanAsync(It.IsAny<SyncScope>(), It.IsAny<SyncOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncScope scope, SyncOptions options, CancellationToken _) => PlanFor(scope, options, StepsFor(options)));
        _sync.Setup(s => s.ExecuteAsync(It.IsAny<SyncPlan>(), It.IsAny<IProgress<LibrarySyncProgress>>(), It.IsAny<CancellationToken>()))
            .Returns(async (SyncPlan p, IProgress<LibrarySyncProgress>? _, CancellationToken ct) =>
            {
                firstRunToken = ct;
                entered.TrySetResult();
                await release.Task;
                return ReportFor(p, 0, false, []);
            });

        var first = vm.DownloadMissingMetadataCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        vm.IsSyncRunning.Should().BeTrue("a run is in flight");
        vm.DownloadMissingMetadataCommand.CanExecute(null).Should().BeFalse(
            "the button that starts a run is off while one is running");

        // WaitAsync, not a bare await: without the guard the second call reaches the gated mock
        // and blocks on it forever, and a hanging test says nothing — this makes it fail instead.
        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(10));

        vm.SyncStatus.Should().Be("A metadata sync is already running.",
            "the refusal has to explain itself — the alternative is an exception message about a run the user did not know about");
        _sync.Verify(s => s.ExecuteAsync(It.IsAny<SyncPlan>(), It.IsAny<IProgress<LibrarySyncProgress>>(), It.IsAny<CancellationToken>()), Times.Once,
            "the second press must not start a second run");

        vm.CancelMetadataDownloadCommand.Execute(null);
        firstRunToken.IsCancellationRequested.Should().BeTrue(
            "Cancel must reach the token the run in flight is actually observing — the discovery pre-run included");

        release.TrySetResult();
        await first;

        vm.IsSyncRunning.Should().BeFalse("the run finished, so both buttons come back");
        vm.DownloadMissingMetadataCommand.CanExecute(null).Should().BeTrue();
    }

    /// <summary>
    /// F1. Cancelling is not failing. The service stops cooperatively, swallows the
    /// <see cref="OperationCanceledException"/> and returns a report for the models it did finish —
    /// which are already committed to the database and invisible in the grid until it is rebuilt.
    /// Handing the run's own (by then signalled) token to that rebuild threw the report away and
    /// left the user with "Metadata sync cancelled" and a grid that still says nothing was found.
    /// </summary>
    [Fact]
    public async Task DownloadMissingMetadata_CancelledRunStillRebuildsTheGridAndShowsItsTally()
    {
        var vm = CreateViewModel();
        SyncReport? report = null;

        _sync.Setup(s => s.PlanAsync(It.IsAny<SyncScope>(), It.IsAny<SyncOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncScope scope, SyncOptions options, CancellationToken _) => PlanFor(scope, options, StepsFor(options)));
        _sync.Setup(s => s.ExecuteAsync(It.IsAny<SyncPlan>(), It.IsAny<IProgress<LibrarySyncProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncPlan p, IProgress<LibrarySyncProgress>? _, CancellationToken _) =>
            {
                // The discovery pre-run finishes normally; the user presses Cancel during the run
                // that follows, with two of the three items done.
                if (IsDiscovery(p.Options)) return ReportFor(p, 0, false, []);

                vm.CancelMetadataDownloadCommand.Execute(null);
                report = new SyncReport(
                    p,
                    [new SyncStepReport(SyncStepKind.IdentifyModel, 3, 2, 2, 0, 0)],
                    Failures: [],
                    Cancelled: true,
                    Elapsed: TimeSpan.FromSeconds(4),
                    NewFilesDiscovered: 0);
                return report;
            });

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        _modelSync.Verify(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()), Times.Once,
            "the models identified before the cancel are in the database and stay invisible until the grid is rebuilt");
        vm.SyncStatus.Should().Be(report!.Summary,
            "a cancelled run still has a tally, and the report is the only thing that knows it");
        vm.SyncStatus.Should().Contain("(cancelled)", "the report's own summary says so");
        _settings.Verify(s => s.UpdateLastLibrarySyncAtAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never,
            "a cancelled run is not a full sync, whatever it managed to finish");
    }

    /// <summary>
    /// F2. The per-tile fetch owns the service from the moment it starts planning, but
    /// <c>ILibrarySyncService.IsRunning</c> only turns true once <c>ExecuteAsync</c> is reached.
    /// A bulk press in that window used to sail past the guard and hit the service's throw.
    /// </summary>
    [Fact]
    public async Task DownloadMissingMetadata_IsRefusedWhileAPerTileFetchIsStillPlanning()
    {
        var vm = CreateViewModel();
        var planning = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        _sync.Setup(s => s.PlanAsync(It.IsAny<SyncScope>(), It.IsAny<SyncOptions>(), It.IsAny<CancellationToken>()))
            .Returns(async (SyncScope s, SyncOptions o, CancellationToken _) =>
            {
                planning.TrySetResult();
                await release.Task;
                return PlanFor(s, o, IdentifyStep(1));
            });
        _sync.Setup(s => s.ExecuteAsync(It.IsAny<SyncPlan>(), It.IsAny<IProgress<LibrarySyncProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncPlan p, IProgress<LibrarySyncProgress>? _, CancellationToken _) =>
                new SyncReport(p, [new SyncStepReport(SyncStepKind.IdentifyModel, 1, 1, 0, 0, 0)], [], false, TimeSpan.Zero, 0));

        var tileRun = vm.DownloadMetadataForTileAsync(CreateTile(modelId: 42));
        await planning.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // WaitAsync: without the fix the bulk run reaches the gated PlanAsync and blocks forever.
        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(10));

        vm.SyncStatus.Should().Be("A metadata sync is already running.");
        _sync.Verify(s => s.PlanAsync(It.IsAny<SyncScope>(), It.IsAny<SyncOptions>(), It.IsAny<CancellationToken>()), Times.Once,
            "the bulk press must not queue a second plan behind the tile's");

        release.TrySetResult();
        await tileRun;
    }

    /// <summary>
    /// R10. Same single-flight rule from the other entry point: the detail panel's button runs
    /// through the same service, so it is refused while a bulk run is going — and while it runs,
    /// the bulk button is off.
    /// </summary>
    [Fact]
    public async Task DownloadMetadataForTile_IsRefusedWhileTheServiceIsAlreadyRunning()
    {
        var vm = CreateViewModel();
        _sync.SetupGet(s => s.IsRunning).Returns(true);

        var outcome = await vm.DownloadMetadataForTileAsync(CreateTile(modelId: 42));

        outcome.Applied.Should().BeFalse();
        outcome.Report.Should().BeNull("the run never started");
        vm.SyncStatus.Should().Be("A metadata sync is already running.");
        _sync.Verify(s => s.PlanAsync(It.IsAny<SyncScope>(), It.IsAny<SyncOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DownloadMetadataForTile_TurnsTheBulkButtonOffWhileItRuns()
    {
        var vm = CreateViewModel();
        var canExecuteDuringRun = true;

        _sync.Setup(s => s.PlanAsync(It.IsAny<SyncScope>(), It.IsAny<SyncOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncScope s, SyncOptions o, CancellationToken _) => PlanFor(s, o, IdentifyStep(1)));
        _sync.Setup(s => s.ExecuteAsync(It.IsAny<SyncPlan>(), It.IsAny<IProgress<LibrarySyncProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncPlan p, IProgress<LibrarySyncProgress>? _, CancellationToken _) =>
            {
                canExecuteDuringRun = vm.DownloadMissingMetadataCommand.CanExecute(null);
                return new SyncReport(p, [new SyncStepReport(SyncStepKind.IdentifyModel, 1, 1, 0, 0, 0)], [], false, TimeSpan.Zero, 0);
            });

        await vm.DownloadMetadataForTileAsync(CreateTile(modelId: 42));

        canExecuteDuringRun.Should().BeFalse("one tile's fetch owns the single-flight service for its duration");
        vm.IsSyncRunning.Should().BeFalse("and gives it back when it is done");
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
