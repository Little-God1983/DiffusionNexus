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

    /// <summary>
    /// How the mocked settings read behaves. Null means "hand back <see cref="_savedSettings"/>
    /// immediately"; a test that cares about <i>when</i> the read happens supplies its own.
    /// </summary>
    private Func<Task<AppSettings>>? _settingsRead;

    private LoraViewerViewModel CreateViewModel(bool withSyncService = true)
    {
        _modelSync.Setup(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<InstalledModelFile>());
        _unitOfWork.SetupGet(u => u.Models).Returns(_models.Object);
        _unitOfWork.SetupGet(u => u.SyncStates).Returns(_syncStates.Object);
        _settings.Setup(s => s.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .Returns(() => _settingsRead is null ? Task.FromResult(_savedSettings) : _settingsRead());

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
        string? discoverAbortReason = null,
        string? runAbortReason = null,
        int repointed = 0,
        bool runHasNoSteps = false,
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
                        discoverUnexpected, discoverUnexpected > 0 ? "scan: NullReferenceException" : null,
                        discoverAbortReason, repointed)
                    : ReportFor(plan, discovered: 0, cancelled, failures, runElapsed,
                        abortReason: runAbortReason, noSteps: runHasNoSteps);
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
        string? firstUnexpectedError = null,
        string? abortReason = null,
        int repointed = 0,
        bool noSteps = false)
        => new(
            plan,
            noSteps
                // An abort at the API-key read or the first step's selection: no step ever tallied.
                ? []
                : plan.Steps.Select(s =>
                {
                    var failed = failures.Count(f => f.Step == s.Kind);
                    return new SyncStepReport(s.Kind, s.Count, s.Count, Math.Max(0, s.Count - failed), 0, failed);
                }).ToList(),
            failures,
            Cancelled: cancelled,
            Elapsed: elapsed ?? TimeSpan.FromSeconds(12),
            NewFilesDiscovered: IsDiscovery(plan.Options) ? discovered : 0,
            UnexpectedFailures: unexpected,
            FirstUnexpectedError: firstUnexpectedError,
            AbortReason: abortReason,
            FilesRepointed: IsDiscovery(plan.Options) ? repointed : 0);

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

    /// <summary>
    /// #541. The pre-run counterpart of the backstop: before the run is reached, only the scan's
    /// own counts may owe a rebuild. With an empty scan, a cancelled dialog must cost nothing —
    /// this pins the placement of <c>rebuildOwed = true</c> AFTER the dialog, where the run
    /// actually starts. Hoisting it up to "the dialog has answered" (a natural reading of "the
    /// run is confirmed") would buy a full wasted re-projection on every cancel, with no other
    /// test failing.
    /// </summary>
    [Fact]
    public async Task DownloadMissingMetadata_CancellingTheDialogAfterAnEmptyScanDoesNotRebuild()
    {
        var vm = CreateViewModel();
        SetupSyncService();   // discovered: 0, repointed: 0
        _planDialogAnswer = _ => Task.FromResult(SyncPlanDialogResult.Cancelled());

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        _modelSync.Verify(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()), Times.Never,
            "nothing changed in the database — a re-projection would be pure cost on every declined dialog");
        vm.SyncStatus.Should().Be("Sync cancelled — nothing was run.",
            "and the exit under test is the one that was actually taken");
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

    /// <summary>
    /// #537. The scan's OTHER write: a moved file hash-matched to an invalid-path row is
    /// re-pointed, in the same commit that inserts new models — and repoint candidates are exactly
    /// the rows the grid hides, so the twelve models just came back into the database. Gating the
    /// rebuild on "discovered > 0" alone left them off screen behind a status line claiming
    /// nothing was run, until a manual Refresh.
    /// </summary>
    [Theory]
    [InlineData(12, "Sync cancelled — the scan re-linked 12 moved files.")]
    [InlineData(1, "Sync cancelled — the scan re-linked 1 moved file.")]
    public async Task DownloadMissingMetadata_CancellingTheDialogStillRebuildsAfterARepointOnlyScan(
        int repointed, string expectedStatus)
    {
        var vm = CreateViewModel();
        SetupSyncService(repointed: repointed);   // discovered: 0 — the scan only re-pointed
        _planDialogAnswer = _ => Task.FromResult(SyncPlanDialogResult.Cancelled());

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        _modelSync.Verify(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()), Times.Once,
            "the re-pointed rows are valid again and stay invisible until the grid is re-projected");
        vm.SyncStatus.Should().Be(expectedStatus,
            "\"nothing was run\" is false once the scan has re-linked files — it is what the button just did");
    }

    /// <summary>
    /// A scan can do both in one pass. The wording was exclusive (`if added … else if re-linked`),
    /// so a scan that added 3 files AND re-linked 12 told the user about the 3 only — the twelve
    /// reappeared models were back on screen unexplained, the #537 complaint one branch over.
    /// </summary>
    [Theory]
    [InlineData(3, 12, "Sync cancelled — the scan added 3 new files and re-linked 12 moved files.")]
    [InlineData(1, 1, "Sync cancelled — the scan added 1 new file and re-linked 1 moved file.")]
    public async Task DownloadMissingMetadata_CancellingTheDialogAfterAScanThatAddedAndRelinkedStatesBoth(
        int discovered, int repointed, string expectedStatus)
    {
        var vm = CreateViewModel();
        SetupSyncService(discovered: discovered, repointed: repointed);
        _planDialogAnswer = _ => Task.FromResult(SyncPlanDialogResult.Cancelled());

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        vm.SyncStatus.Should().Be(expectedStatus,
            "both of the scan's writes changed what the grid shows, so both belong in the answer");
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

    /// <summary>
    /// The same rebuild is owed when the RUN dies midway, not only when the scan added rows. Each
    /// item commits in its own scope, so an ExecuteAsync that escapes leaves, say, 200 freshly
    /// identified models already durable. The real service no longer escapes (#535 made it total —
    /// an abort comes back as a report), so what this pins is the ViewModel's own defense: a
    /// service regression that throws again must still get the committed work onto the grid.
    /// </summary>
    [Fact]
    public async Task DownloadMissingMetadata_RebuildsWhenTheRunDiesMidway()
    {
        var vm = CreateViewModel();
        SetupSyncService();   // discovered: 0 — only the run wrote anything
        _throwOnExecuteCall = 2;
        _executeThrow = new InvalidOperationException("database is locked");

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        _modelSync.Verify(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()), Times.Once,
            "whatever the run finished before dying is committed, and the grid has to show it");
        vm.SyncStatus.Should().StartWith("Sync error:", "the rebuild must not soften the error verdict");
    }

    /// <summary>
    /// …but a run the single-flight gate refused never got the service at all, so this press wrote
    /// nothing beyond the scan — with an empty scan there is nothing new to re-project.
    /// </summary>
    [Fact]
    public async Task DownloadMissingMetadata_DoesNotRebuildWhenARefusedRunFollowedAnEmptyScan()
    {
        var vm = CreateViewModel();
        SetupSyncService();   // discovered: 0
        _throwOnExecuteCall = 2;   // _executeThrow defaults to SyncAlreadyRunningException

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        _modelSync.Verify(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()), Times.Never,
            "the refusal happened at the gate, before any work — a full grid re-projection would be pure cost");
        vm.SyncStatus.Should().Be("A metadata sync is already running.",
            "#541: without this the test passes vacuously — any exit before the run also never rebuilds, " +
            "so the status proves the refusal catch (the reset under test) actually executed");
    }

    /// <summary>
    /// #537, the completed-run half: five models just reappeared, so the verdict may not be
    /// "Library is up to date — nothing to do" even though nothing was planned in any step.
    /// </summary>
    [Fact]
    public async Task DownloadMissingMetadata_ARepointOnlyScanIsNotReportedAsUpToDate()
    {
        var vm = CreateViewModel();
        _identifyCount = 0;              // no step has work — only the scan's repoints happened
        SetupSyncService(repointed: 5);

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        vm.SyncStatus.Should().Contain("5 moved files re-linked",
            "five models just came back on screen, and the status line is where the user learns why");
    }

    /// <summary>
    /// #540. Task.Run with an already-signalled token never invokes the delegate — an OCE at the
    /// run's own await proves ExecuteAsync was never entered, because a cancellation INSIDE the
    /// service comes back as a Cancelled report, not a throw. That proof licenses waiving the
    /// owed rebuild there and only there: the shared OCE catch must not, because an OCE later in
    /// the flow (a dispatcher dying mid-swap) can arrive after a fully durable run.
    /// </summary>
    [Fact]
    public async Task DownloadMissingMetadata_APreStartCancelDoesNotPayForARebuild()
    {
        var vm = CreateViewModel();
        SetupSyncService();   // discovered: 0 — nothing but the never-started run could owe one
        _throwOnExecuteCall = 2;
        _executeThrow = new TaskCanceledException();

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        vm.SyncStatus.Should().Be("Metadata sync cancelled");
        _modelSync.Verify(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()), Times.Never,
            "the run never entered the service and the scan was empty — a full re-projection would be pure cost");
    }

    /// <summary>
    /// #540. The backstop is a multi-second, UI-thread tile swap on a big library, and it used to
    /// run after the finally had already dropped the overlay — while both sync buttons still
    /// refused input. An unattributed dead zone right after a status line said the operation was
    /// over. The overlay now stays up over the backstop and says what is happening.
    /// </summary>
    [Fact]
    public async Task DownloadMissingMetadata_TheBackstopRebuildRunsUnderTheOverlay()
    {
        var vm = CreateViewModel();
        SetupSyncService();
        _throwOnExecuteCall = 2;
        _executeThrow = new InvalidOperationException("database is locked");

        bool? busyDuringBackstop = null;
        string? messageDuringBackstop = null;
        _modelSync.Setup(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                busyDuringBackstop = vm.IsBusy;
                messageDuringBackstop = vm.BusyMessage;
            })
            .ReturnsAsync(Array.Empty<InstalledModelFile>());

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        busyDuringBackstop.Should().BeTrue("a multi-second tile swap with no overlay reads as a hang");
        messageDuringBackstop.Should().NotBeNullOrEmpty("the overlay must say what it is doing");
        vm.IsBusy.Should().BeFalse("and it comes down when everything is finished");
        vm.BusyMessage.Should().BeNull();
    }

    /// <summary>
    /// #540. RefreshAsync had no CanExecute, so its button was clickable through a whole sync and
    /// its unwind — and a press started a second full-library read whose "Loaded N models" then
    /// overwrote the sync's verdict in the status bar. Off while a sync runs, like both sync
    /// buttons; the detail-deleted fallback already handles CanExecute being false.
    /// </summary>
    [Fact]
    public async Task Refresh_IsOffWhileASyncRuns()
    {
        var vm = CreateViewModel();
        SetupSyncService();

        var canRefreshDuringSync = true;
        _planDialogAnswer = dialogVm =>
        {
            canRefreshDuringSync = vm.RefreshCommand.CanExecute(null);
            return Task.FromResult(dialogVm.BuildResult());
        };

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        canRefreshDuringSync.Should().BeFalse("a refresh mid-sync erases the verdict and doubles the DB load");
        vm.RefreshCommand.CanExecute(null).Should().BeTrue("and back on the moment the sync is over");
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
        _executed[^1].Options.ThumbnailConcurrency.Should().Be(6, "and therefore on the ones the run executes");
        _executed[^1].Options.Policy.NotIdentifiedRetryAfter.Should().Be(TimeSpan.FromDays(14));
        _executed[^1].Options.Policy.ErrorRetryAfter.Should().Be(TimeSpan.FromDays(3));
    }

    /// <summary>
    /// F13. Every Force toggle already re-planned with the exact options the dialog hands back, and
    /// PlanAsync is a repository query over the whole library per requested step. Pressing Start
    /// without changing anything — the common case, since every row with work is pre-ticked — used
    /// to pay for that pass a second time on the button's critical path.
    /// </summary>
    [Fact]
    public async Task DownloadMissingMetadata_RunsTheDialogsOwnPlanInsteadOfPlanningAThirdTime()
    {
        var vm = CreateViewModel();
        SetupSyncService();

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        _planned.Should().HaveCount(2, "the discovery plan and the one behind the dialog — and no third pass");
        _calls.Should().ContainInOrder("plan-dialog", "execute:run")
            .And.NotContainInOrder("plan-dialog", "plan:run");
        _executed[^1].Steps.Select(s => s.Kind).Should().BeEquivalentTo(new[] { SyncStepKind.IdentifyModel },
            "the dialog's plan comes back filtered to the ticked kinds");
    }

    /// <summary>…and plans again when the dialog cannot vouch for its counts (a failed re-plan).</summary>
    [Fact]
    public async Task DownloadMissingMetadata_PlansAgainWhenTheDialogHandsBackNoPlan()
    {
        var vm = CreateViewModel();
        SetupSyncService();
        _planDialogAnswer = dialogVm => Task.FromResult(dialogVm.BuildResult() with { Plan = null });

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        _planned.Should().HaveCount(3, "without a plan to run, the flow has to make one");
        _calls.Should().ContainInOrder("plan-dialog", "plan:run", "execute:run");
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
    /// made the viewer announce a full sync for metadata that had never been fetched at all. The
    /// yardstick is the dialog's rows: a kind the user UNTICKED while it had work makes the run
    /// partial, and partial runs leave the timestamp alone.
    /// </summary>
    [Fact]
    public async Task DownloadMissingMetadata_DoesNotStampAFullSyncWhenAKindWithWorkWasUnticked()
    {
        var vm = CreateViewModel();
        _otherStepCount = 2;   // every kind has work…
        _planDialogAnswer = dialogVm =>
        {
            // …but the user opts tags out of the run.
            dialogVm.Rows.Single(r => r.Kind == SyncStepKind.FetchTags).IsSelected = false;
            return Task.FromResult(dialogVm.BuildResult());
        };
        SetupSyncService();

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        _executed[^1].Options.Steps.Should().NotContain(SyncStepKind.FetchTags,
            "the untick must actually have excluded the step for this test to mean anything");
        _settings.Verify(s => s.UpdateLastLibrarySyncAtAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a run that skipped tags the library still needed may not present itself as a full sync next week");
    }

    /// <summary>
    /// The counterpart: a kind with NOTHING to do is covered by doing nothing. BuildResult drops
    /// zero-count kinds from the chosen set, so demanding all four kinds would mean a library
    /// where tags/images/thumbnails are already complete could never refresh its stamp again.
    /// </summary>
    [Fact]
    public async Task DownloadMissingMetadata_StampsWhenEveryKindWithWorkRan()
    {
        var vm = CreateViewModel();
        SetupSyncService();   // only the identify row has work; the other three have nothing to do

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        _executed[^1].Options.Steps.Should().BeEquivalentTo(new[] { SyncStepKind.IdentifyModel });
        _settings.Verify(s => s.UpdateLastLibrarySyncAtAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "after this run the library is as synced as four ticked boxes would have left it");
    }

    /// <summary>
    /// #535. ExecuteAsync is total now: a throw outside its item loop comes back as
    /// <see cref="SyncReport.AbortReason"/> instead of escaping. The flow treats that report as
    /// what it is — a record of a run that died midway: the report dialog still opens over it and
    /// the status line names the reason, but "last full sync" is not stamped, however completely
    /// the kinds were ticked, because the run did not cover what the user agreed to.
    /// </summary>
    [Fact]
    public async Task DownloadMissingMetadata_AnAbortedRunShowsItsReportAndDoesNotStampFullSync()
    {
        var vm = CreateViewModel();
        _otherStepCount = 2;   // every kind has work and is ticked — without the abort this stamps
        SetupSyncService(runAbortReason: "Unexpected InvalidOperationException: database is locked");

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        _reportDialogVm.Should().NotBeNull(
            "what the run finished before dying is committed, and the report is the only record of it");
        vm.SyncStatus.Should().StartWith("Sync aborted").And.Contain("database is locked",
            "the verdict must name the failure, not read like a completed run");
        _settings.Verify(s => s.UpdateLastLibrarySyncAtAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Never, "a run that died midway did not cover what the user agreed to");
        _modelSync.Verify(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()), Times.Once,
            "the committed work reaches the grid once — the run path's rebuild, no backstop second pass");
    }

    /// <summary>
    /// The abort verdict must not drop the run's other bad news. The abort branch used to return
    /// early with reason + Summary alone, skipping the ` · N failed`, ` · N moved files re-linked`
    /// and unexpected-failure suffixes — the run where the most went wrong was the one whose
    /// status line said the least. And Summary already carries "(aborted)", so the word appeared
    /// twice.
    /// </summary>
    [Fact]
    public async Task DownloadMissingMetadata_AnAbortedRunStillStatesItsFailuresAndRepoints()
    {
        var vm = CreateViewModel();
        SetupSyncService(
            repointed: 5,
            runAbortReason: "Unexpected InvalidOperationException: database is locked",
            failures:
            [
                new SyncFailure(SyncStepKind.IdentifyModel, 1, "a.safetensors", "timeout"),
                new SyncFailure(SyncStepKind.IdentifyModel, 2, "b.safetensors", "timeout"),
            ]);

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        vm.SyncStatus.Should().Be(
            "Sync aborted — Unexpected InvalidOperationException: database is locked · " +
            "Discovered 0 · Identified 1/3 · 2 failed · 5 moved files re-linked",
            "the abort leads, but the failures and repoints the run racked up still follow");
        (vm.SyncStatus!.Length - vm.SyncStatus.Replace("aborted", "").Length).Should().Be("aborted".Length,
            "the lead already says aborted; Summary's own \"(aborted)\" marker must not repeat it");
    }

    /// <summary>
    /// A run that died before any step tallied — the API-key read, the first step's selection —
    /// has a report whose table is empty. The status line already leads with the abort; opening a
    /// modal dialog over an empty table on top of it says nothing the line did not. The aborted
    /// SCAN path already behaves this way (status line only), and an abort with committed work
    /// (non-empty steps) or scan failures still opens the dialog, per the test above.
    /// </summary>
    [Fact]
    public async Task DownloadMissingMetadata_AnAbortBeforeAnyStepSkipsTheEmptyReportDialog()
    {
        var vm = CreateViewModel();
        SetupSyncService(
            runAbortReason: "Unexpected InvalidOperationException: database is locked",
            runHasNoSteps: true);

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        _reportDialogVm.Should().BeNull("there is no row the dialog could show that the status line has not already said");
        vm.SyncStatus.Should().StartWith("Sync aborted").And.Contain("database is locked",
            "suppressing the empty dialog must not soften the verdict");
    }

    /// <summary>
    /// #535. The scan pre-run aborting keeps the abort semantics it had when the service still
    /// threw: the flow stops before the dialog, because a question built on counts a broken scan
    /// produced would put the user's yes on bad numbers. (Ordinary scan failures — an unreadable
    /// folder — still proceed and are folded into the report; an abort is a bug, not a verdict.)
    /// </summary>
    [Fact]
    public async Task DownloadMissingMetadata_AnAbortedScanStopsBeforeTheDialog()
    {
        var vm = CreateViewModel();
        SetupSyncService(discoverAbortReason: "Unexpected InvalidOperationException: database is locked");

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        _calls.Should().NotContain("plan-dialog");
        vm.SyncStatus.Should().Be("Sync error: Unexpected InvalidOperationException: database is locked");
        _sync.Verify(s => s.ExecuteAsync(It.IsAny<SyncPlan>(), It.IsAny<IProgress<LibrarySyncProgress>>(), It.IsAny<CancellationToken>()),
            Times.Once, "only the scan ran; the flow stopped there exactly as it did when the scan still threw");
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
    /// #539. The run-path rebuild is a DB read at the peak of WAL contention — the run has been
    /// writing for minutes. Unguarded, its throw landed in the generic catch: a SUCCESSFUL run's
    /// verdict was replaced by "Sync error: …", the report dialog was skipped (the report lost),
    /// and the finally then retried the identical rebuild anyway. A failed rebuild costs the
    /// rebuild: the verdict stands, the report shows, and the backstop gets one deliberate retry.
    /// </summary>
    [Fact]
    public async Task DownloadMissingMetadata_AFailedRebuildDoesNotEraseTheRunsVerdict()
    {
        var vm = CreateViewModel();
        SetupSyncService();
        _modelSync.SetupSequence(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"))
            .ReturnsAsync(Array.Empty<InstalledModelFile>());

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        _reportDialogVm.Should().NotBeNull("the run succeeded; losing its report to a grid read is out of proportion");
        vm.SyncStatus.Should().Be(RunReport().Summary,
            "the verdict belongs to the run, not to the rebuild that failed after it");
        _modelSync.Verify(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2),
            "the backstop gives the failed rebuild one deliberate retry — which here succeeds");
    }

    /// <summary>
    /// #539/#541, the OCE door. An OperationCanceledException out of the rebuild (dispatcher
    /// shutdown mid-swap) is a rebuild failure, not the user cancelling the sync — the shared OCE
    /// catch used to relabel the whole successful run "Metadata sync cancelled" over it, and the
    /// owed rebuild still had to happen.
    /// </summary>
    [Fact]
    public async Task DownloadMissingMetadata_AnOceFromTheRebuildIsARebuildFailureNotACancelledSync()
    {
        var vm = CreateViewModel();
        SetupSyncService();
        _modelSync.SetupSequence(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException())
            .ReturnsAsync(Array.Empty<InstalledModelFile>());

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        vm.SyncStatus.Should().Be(RunReport().Summary, "nobody cancelled anything");
        _reportDialogVm.Should().NotBeNull();
        _modelSync.Verify(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2),
            "the backstop still owes — and delivers — the rebuild");
    }

    /// <summary>When the retry fails too, the user must learn the grid is stale — appended, not replacing the verdict.</summary>
    [Fact]
    public async Task DownloadMissingMetadata_ABackstopRebuildFailureIsSaidOutLoud()
    {
        var vm = CreateViewModel();
        SetupSyncService();
        _modelSync.Setup(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        await vm.DownloadMissingMetadataCommand.ExecuteAsync(null);

        vm.SyncStatus.Should().StartWith(RunReport().Summary, "the run's verdict still leads")
            .And.Contain("grid refresh failed", "a stale grid without a word would read as lost work");
        _reportDialogVm.Should().NotBeNull("the report itself is intact — only the grid view is behind");
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
        // The dialog withholds its plan whenever a re-plan failed and left its counts stale, and
        // the flow then plans again — which is the pass that finds the work already done.
        _planDialogAnswer = dialogVm => Task.FromResult(dialogVm.BuildResult() with { Plan = null });

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
        _modelSync.Verify(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()), Times.Never,
            "#541: this door exits before the run too, and the empty scan owes no rebuild");
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
        options.ThumbnailConcurrency.Should().Be(6,
            "one model can have a dozen due images, and someone who set 'thumbnail downloads in parallel = 1' " +
            "on a metered connection meant it for this button too — not only for the bulk run");
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

    /// <summary>
    /// #535/#536. ExecuteAsync is total now, so a run that dies midway reaches this path as a
    /// report with <see cref="SyncReport.AbortReason"/> instead of an exception. That report is a
    /// failed ask, not an answer — the outcome has to carry the fault out of the ViewModel so the
    /// detail view can say "failed" rather than "No metadata found on Civitai for this file."
    /// </summary>
    [Fact]
    public async Task DownloadMetadataForTile_AnAbortedRunIsFaultedNotAVerdictAboutCivitai()
    {
        var vm = CreateViewModel();

        _sync.Setup(s => s.PlanAsync(It.IsAny<SyncScope>(), It.IsAny<SyncOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncScope s, SyncOptions o, CancellationToken _) => PlanFor(s, o, IdentifyStep(1)));
        _sync.Setup(s => s.ExecuteAsync(It.IsAny<SyncPlan>(), It.IsAny<IProgress<LibrarySyncProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncPlan p, IProgress<LibrarySyncProgress>? _, CancellationToken _) => new SyncReport(
                p, [], [], Cancelled: false, Elapsed: TimeSpan.Zero, NewFilesDiscovered: 0,
                AbortReason: "Unexpected InvalidOperationException: database is locked"));

        var outcome = await vm.DownloadMetadataForTileAsync(CreateTile(modelId: 42));

        outcome.Applied.Should().BeFalse("nothing succeeded before the run died");
        outcome.Faulted.Should().BeTrue("a failed ask is not an answer about Civitai");
    }

    /// <summary>An item that failed with an exception no step claimed is the same kind of non-answer.</summary>
    [Fact]
    public async Task DownloadMetadataForTile_AnUnexpectedItemFailureIsFaultedToo()
    {
        var vm = CreateViewModel();

        _sync.Setup(s => s.PlanAsync(It.IsAny<SyncScope>(), It.IsAny<SyncOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncScope s, SyncOptions o, CancellationToken _) => PlanFor(s, o, IdentifyStep(1)));
        _sync.Setup(s => s.ExecuteAsync(It.IsAny<SyncPlan>(), It.IsAny<IProgress<LibrarySyncProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncPlan p, IProgress<LibrarySyncProgress>? _, CancellationToken _) => new SyncReport(
                p, [new SyncStepReport(SyncStepKind.IdentifyModel, 1, 1, 0, 0, 1)],
                [new SyncFailure(SyncStepKind.IdentifyModel, 42, "a.safetensors", "Unexpected NullReferenceException: boom")],
                Cancelled: false, Elapsed: TimeSpan.Zero, NewFilesDiscovered: 0,
                UnexpectedFailures: 1, FirstUnexpectedError: "Unexpected NullReferenceException: boom"));

        var outcome = await vm.DownloadMetadataForTileAsync(CreateTile(modelId: 42));

        outcome.Applied.Should().BeFalse();
        outcome.Faulted.Should().BeTrue("a bug in the one item that was asked about is not \"Civitai has nothing\"");
    }

    /// <summary>A genuine no — the step asked and Civitai had nothing — must NOT read as a fault.</summary>
    [Fact]
    public async Task DownloadMetadataForTile_AGenuineNoFromCivitaiIsNotFaulted()
    {
        var vm = CreateViewModel();

        _sync.Setup(s => s.PlanAsync(It.IsAny<SyncScope>(), It.IsAny<SyncOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncScope s, SyncOptions o, CancellationToken _) => PlanFor(s, o, IdentifyStep(1)));
        _sync.Setup(s => s.ExecuteAsync(It.IsAny<SyncPlan>(), It.IsAny<IProgress<LibrarySyncProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncPlan p, IProgress<LibrarySyncProgress>? _, CancellationToken _) => new SyncReport(
                p, [new SyncStepReport(SyncStepKind.IdentifyModel, 1, 1, 0, 1, 0)], [],
                Cancelled: false, Elapsed: TimeSpan.Zero, NewFilesDiscovered: 0));

        var outcome = await vm.DownloadMetadataForTileAsync(CreateTile(modelId: 42));

        outcome.Faulted.Should().BeFalse("the run completed cleanly; \"No metadata found\" is the honest message here");
    }

    /// <summary>
    /// #536, one notch narrower: an ordinary recorded failure — the step asked and the ask itself
    /// failed (HTTP 500, timeout, disk error) — is a non-answer too. It is expected (the step
    /// claimed it, so it is not an UnexpectedFailure) and it is not an abort, so it slipped past
    /// <c>Faulted</c> and the detail view said "No metadata found on Civitai for this file." over
    /// a transport failure. Disjointness with the honest no holds in the real step too: identify
    /// records "checked, not on Civitai" as a success/skip, never as a failure.
    /// </summary>
    [Fact]
    public async Task DownloadMetadataForTile_AnOrdinaryFailedAskIsFaultedNotAVerdictAboutCivitai()
    {
        var vm = CreateViewModel();

        _sync.Setup(s => s.PlanAsync(It.IsAny<SyncScope>(), It.IsAny<SyncOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncScope s, SyncOptions o, CancellationToken _) => PlanFor(s, o, IdentifyStep(1)));
        _sync.Setup(s => s.ExecuteAsync(It.IsAny<SyncPlan>(), It.IsAny<IProgress<LibrarySyncProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncPlan p, IProgress<LibrarySyncProgress>? _, CancellationToken _) => new SyncReport(
                p, [new SyncStepReport(SyncStepKind.IdentifyModel, 1, 1, 0, 0, 1)],
                [new SyncFailure(SyncStepKind.IdentifyModel, 42, "a.safetensors",
                    "Response status code does not indicate success: 500")],
                Cancelled: false, Elapsed: TimeSpan.Zero, NewFilesDiscovered: 0));

        var outcome = await vm.DownloadMetadataForTileAsync(CreateTile(modelId: 42));

        outcome.Applied.Should().BeFalse();
        outcome.Faulted.Should().BeTrue("a failed ask is not an answer about Civitai");
        outcome.FaultReason.Should().Contain("500", "the message must name the failure, not a verdict");
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

    // ------------------------------------------------------------------------------- startup cost

    /// <summary>
    /// F14. The startup read wants two ints, but <c>AppSettingsService.GetSettingsAsync</c> clears
    /// the change tracker, loads the whole settings graph and performs up to three writes — and
    /// SQLite has no true async, so awaited bare from the constructor all of that ran inline on the
    /// UI thread before the first real yield. A startup hitch, and a database <i>write</i>, during
    /// viewer construction.
    /// </summary>
    /// <remarks>
    /// The gate blocks synchronously, the way a SQLite call does, and is self-bounding: a
    /// regression makes this test take five seconds and fail on <c>readReleased</c>, rather than
    /// hang and say nothing.
    /// </remarks>
    [Fact]
    public async Task ConstructingTheViewer_DoesNotRunTheSettingsReadInline()
    {
        using var release = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        var readReleased = false;

        _settingsRead = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            Volatile.Write(ref readReleased, true);
            return Task.FromResult(_savedSettings);
        };

        var vm = CreateViewModel();

        Volatile.Read(ref readReleased).Should().BeFalse(
            "the constructor must return while the settings read is still going, not sit on it");
        entered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the read was still started, just not inline");

        release.Set();
        await vm.ScrollRetryPolicyLoad.WaitAsync(TimeSpan.FromSeconds(10));
        vm.ScrollRetryPolicyLoad.IsCompletedSuccessfully.Should().BeTrue("and it does finish");
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
