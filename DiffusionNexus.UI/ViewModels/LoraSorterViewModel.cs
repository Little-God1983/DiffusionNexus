using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Service.Services.IO;
using DiffusionNexus.UI.Helpers;
using DiffusionNexus.UI.Services.Lora.Sorting;
using DiffusionNexus.UI.Utilities;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the LoRA Sorter view: picks a source folder, computes a live preview of
/// where every LoRA would land (spec §7.2), gates the run on free disk space, and executes
/// the move/copy via <see cref="LoraSortExecutor"/>.
/// </summary>
public partial class LoraSorterViewModel : BusyViewModelBase
{
    private const long SafetyMarginBytes = 1L << 30; // 1 GB
    private const string LogSource = "LoraSorter";

    /// <summary>Heartbeat cadence for the unknown-file resolution loop's progress log line.</summary>
    private const int ResolveLogInterval = 50;

    private static readonly string[] ModelExtensions = [".safetensors", ".ckpt", ".pt", ".pth"];

    private readonly IAppSettingsService? _settingsService;
    private readonly IUnifiedLogger? _logger;
    private readonly ILocalPathUpdater _pathUpdater;
    private readonly SorterMetadataResolver _metadataResolver;
    private readonly IFileOperations _fileOperations;
    private readonly Func<string, long> _getAvailableSpace;
    private readonly Func<string, string> _hashFile;
    private readonly Func<string, bool> _fileExistsOnDisk;
    private readonly string _historyDirectory;

    /// <summary>Post-run "delete empty source folders" step, injected so the failure path is
    /// testable — an undeletable folder cannot be created reliably from a test.</summary>
    private readonly Func<string, CancellationToken, Task> _deleteEmptyDirectories;

    /// <summary>
    /// Loads the DB-known installed files. Injected rather than reached through
    /// <c>IModelSyncService</c> directly, because that instance is shared: it and
    /// <c>IUnitOfWork</c> are both transient while <c>LoraViewerViewModel</c> is scoped, so the
    /// viewer resolves exactly one sync service — and therefore one <c>DbContext</c> — for the
    /// whole session and forwards that same instance here. Preview passes are started
    /// fire-and-forget from three option hooks and <c>RunBusyAsync</c> has no re-entrancy guard, so
    /// two passes could each issue a multi-second AsSplitQuery on that one context and hit
    /// "A second operation was started on this context instance". Every other bulk consumer in
    /// <c>LoraViewerViewModel</c> (LoadCachedTilesAsync, DownloadMissingMetadataAsync,
    /// RebuildTilesFromDatabaseAsync) resolves a fresh scope for exactly this reason; the
    /// production wiring passes a delegate that does the same. Null only when no sync service was
    /// supplied at all (design time).
    /// </summary>
    private readonly Func<CancellationToken, Task<IReadOnlyList<InstalledModelFile>>>? _loadCachedFiles;

    private LoraSortPlan? _lastPlan;
    private CancellationTokenSource? _sortCts;

    /// <summary>Set when the post-run empty-folder cleanup failed, so the result banner can say so
    /// without turning a fully successful sort into "Sorting failed".</summary>
    private bool _emptyFolderCleanupFailed;

    /// <summary>Resolved-candidate cache, keyed by the source folder it was resolved for. A
    /// pure option toggle (category/move/target) only re-runs <see cref="LoraSortPlanner"/>
    /// over this cache; only a source-folder change (or <see cref="InitializeAsync"/>)
    /// re-enumerates disk and re-resolves unknown-file metadata.</summary>
    private List<SortCandidate>? _candidateCache;
    private string? _candidateCacheSourceFolder;

    /// <summary>How many files the cached resolution pass had to skip because they could not be
    /// read. Cached alongside the candidates so an option toggle — which does not re-resolve —
    /// keeps showing the same "N files skipped" note instead of silently dropping it.</summary>
    private int _candidateCacheSkippedCount;

    /// <summary>Cancels an in-flight candidate resolution when a newer recompute supersedes it.</summary>
    private CancellationTokenSource? _resolveCts;

    /// <summary>
    /// Monotonic id of the newest preview pass. Every pass captures its own value at entry and
    /// must re-check it before committing <i>anything</i> — cache, plan, tree, summaries, disk
    /// gate. Cancellation alone is not a sufficient guard: a superseded pass whose token is
    /// cancelled between two checkpoints still runs to completion (<see cref="Task.Run(Action, CancellationToken)"/>
    /// only refuses to <i>start</i> a cancelled delegate), reaches the success path and would
    /// permanently overwrite the newer pass's plan and preview tree — including its
    /// <see cref="IsMove"/>/<see cref="IncludeCategory"/> options, which the older
    /// source/target-only commit guard could not see.
    /// </summary>
    private int _previewGeneration;

    /// <summary>
    /// How many preview passes are still running. <see cref="IsBusy"/> — and therefore
    /// <see cref="CanStart"/> — is driven off this rather than off the shared
    /// <c>RunBusyAsync</c>, because that helper's <c>finally</c> dropped the overlay as soon as
    /// the <i>first</i> of several overlapping passes finished: pass A's plan stayed armed,
    /// Start went enabled against it, and pressing it ran A's move plan while the radio already
    /// read Copy.
    /// </summary>
    private int _inFlightPreviews;

    /// <summary>Set by <see cref="CancelSort"/> right before it cancels <see cref="_resolveCts"/>, so
    /// the resulting <see cref="OperationCanceledException"/> catch in <see cref="RecomputePreviewCoreAsync"/>
    /// can tell a genuine user cancel apart from a newer recompute pass silently superseding this one
    /// (which cancels the same field but must NOT surface a "cancelled" status message).</summary>
    private bool _previewCancelledByUser;

    /// <summary>Suppresses the property-change recompute hooks while <see cref="InitializeAsync"/>
    /// is populating <see cref="SourceFolders"/>/<see cref="SelectedSourceFolder"/>, so exactly one
    /// awaited recompute runs instead of a redundant fire-and-forget pass racing it.</summary>
    private bool _isInitializing;

    /// <summary>True when the current <see cref="StatusMessage"/> was set by the "target is
    /// another source" preview warning (as opposed to a sort-run result), so the next recompute
    /// knows it's safe to clear without wiping a "Done: …"/"Cancelled — …" message.</summary>
    private bool _statusMessageIsWarning;

    /// <summary>Raised after a sort run finishes so the parent (Installed tab) can refresh.</summary>
    public event EventHandler? SortCompleted;

    #region Constructors

    /// <summary>Design-time constructor with no services — required because
    /// <see cref="LoraViewerViewModel"/>'s design-time ctor must also build a sorter VM.</summary>
    public LoraSorterViewModel()
    {
        // Latched for the lifetime of the design-time VM: "SelectedSourceFolder = SourceFolders[0]"
        // below fires OnSelectedSourceFolderChanged, which used to start a real recompute against
        // C:\Demo\Loras — disk enumeration and a DriveInfo probe. The ctor then added the demo tree
        // and TransferCount = 17, and that pass's continuation cleared PreviewRoots and reset
        // TransferCount to 0, so the previewer showed an empty tree instead of the demo data. It
        // also meant every "new LoraViewerViewModel()" (whose design-time ctor builds this VM) span
        // up background filesystem I/O with any exception unobserved.
        _isInitializing = true;

        _settingsService = null;
        _logger = null;
        _pathUpdater = new NullLocalPathUpdater();
        // The shared hasher, not yet another private SHA256 copy — this VM's own copy sat next to
        // HashingService and LoraViewerViewModel.ComputeFullSha256, the latter being what the
        // runtime already injects here as hashFile.
        var designTimeHash = (string path) =>
            new HashingService().ComputeFileHash(path, HashingService.HashAlgorithmType.SHA256);
        _metadataResolver = new SorterMetadataResolver(null, () => Task.FromResult<string?>(null),
            SorterMetadataResolver.DefaultCacheDirectory, designTimeHash, logger: null);
        _fileOperations = new FileOperations();
        _getAvailableSpace = DiskUtility.GetAvailableSpace;
        _hashFile = designTimeHash;
        _fileExistsOnDisk = File.Exists;
        _historyDirectory = SortHistoryWriter.DefaultHistoryDirectory;
        _deleteEmptyDirectories = DefaultDeleteEmptyDirectories;
        _loadCachedFiles = null;
        HookIsBusyNotifications();

        SourceFolders.Add(@"C:\Demo\Loras");
        SelectedSourceFolder = SourceFolders[0];
        PreviewRoots.Add(new SortPreviewNodeViewModel { Name = "SDXL 1.0", LoraCount = 12, TotalBytes = 4_200_000_000 });
        PreviewRoots.Add(new SortPreviewNodeViewModel { Name = "Illustrious", LoraCount = 5, TotalBytes = 1_100_000_000 });
        PreviewSummary = "✓ 17 files will move   ·   0 already in place   ·   ✎ 0 auto-renamed · 0 duplicates skipped";
        TransferCount = 17;
        HasEnoughSpace = true;
    }

    /// <summary>Runtime constructor — every I/O seam is injected for testability.</summary>
    public LoraSorterViewModel(
        IAppSettingsService? settingsService,
        IModelSyncService? syncService,
        IUnifiedLogger? logger,
        ILocalPathUpdater pathUpdater,
        SorterMetadataResolver metadataResolver,
        IFileOperations fileOperations,
        Func<string, long> getAvailableSpace,
        Func<string, string> hashFile,
        Func<string, bool> fileExistsOnDisk,
        string historyDirectory,
        Func<string, CancellationToken, Task>? deleteEmptyDirectories = null,
        Func<CancellationToken, Task<IReadOnlyList<InstalledModelFile>>>? loadCachedFiles = null)
    {
        _settingsService = settingsService;
        _logger = logger;
        _pathUpdater = pathUpdater ?? throw new ArgumentNullException(nameof(pathUpdater));
        _metadataResolver = metadataResolver ?? throw new ArgumentNullException(nameof(metadataResolver));
        _fileOperations = fileOperations ?? throw new ArgumentNullException(nameof(fileOperations));
        _getAvailableSpace = getAvailableSpace ?? throw new ArgumentNullException(nameof(getAvailableSpace));
        _hashFile = hashFile ?? throw new ArgumentNullException(nameof(hashFile));
        _fileExistsOnDisk = fileExistsOnDisk ?? throw new ArgumentNullException(nameof(fileExistsOnDisk));
        _historyDirectory = historyDirectory ?? throw new ArgumentNullException(nameof(historyDirectory));
        _deleteEmptyDirectories = deleteEmptyDirectories ?? DefaultDeleteEmptyDirectories;
        _loadCachedFiles = loadCachedFiles
            ?? (syncService is null ? null : syncService.LoadCachedFilesAsync);
        HookIsBusyNotifications();
    }

    /// <summary>The generated <c>OnIsBusyChanged</c> partial hook lives on <see cref="BusyViewModelBase"/>
    /// (where <c>IsBusy</c> is declared) and can't be implemented from this subclass, so
    /// <see cref="CanStart"/> — which depends on <c>IsBusy</c> — wouldn't otherwise get a
    /// <see cref="StartSortingCommand"/> re-evaluation when busy state flips. Subscribing to the
    /// inherited <c>PropertyChanged</c> event instead works from any subclass.</summary>
    private void HookIsBusyNotifications()
    {
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(IsBusy)) return;
            StartSortingCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanStart));
        };
    }

    #endregion

    #region Observable state

    public ObservableCollection<string> SourceFolders { get; } = [];

    [ObservableProperty]
    private string? _selectedSourceFolder;

    /// <summary>Overrides the sort destination; null means "Same as source".</summary>
    [ObservableProperty]
    private string? _customTargetFolder;

    [ObservableProperty]
    private bool _includeCategory = true;

    [ObservableProperty]
    private bool _isMove = true;

    [ObservableProperty]
    private bool _deleteEmptySourceFolders;

    public ObservableCollection<SortPreviewNodeViewModel> PreviewRoots { get; } = [];

    [ObservableProperty]
    private string? _previewSummary;

    [ObservableProperty]
    private string? _diskSummary;

    [ObservableProperty]
    private bool _hasEnoughSpace;

    [ObservableProperty]
    private string? _blockReason;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private int _transferCount;

    public string? EffectiveTargetRoot => string.IsNullOrWhiteSpace(CustomTargetFolder) ? SelectedSourceFolder : CustomTargetFolder;

    public bool CanStart => !IsBusy && HasEnoughSpace && TransferCount > 0 && EffectiveTargetRoot is not null;

    partial void OnIncludeCategoryChanged(bool value)
    {
        // A run-result banner ("Done: …"/"Cancelled — …") survives exactly its own post-run
        // recompute; any further user action clears it, same as a fresh preview warning would.
        ClearRunResultBanner();
        if (_isInitializing) return;
        _ = RecomputePreviewAsync();
    }

    partial void OnIsMoveChanged(bool value)
    {
        ClearRunResultBanner();
        if (_isInitializing) return;
        _ = RecomputePreviewAsync();
    }

    partial void OnSelectedSourceFolderChanged(string? value)
    {
        OnPropertyChanged(nameof(EffectiveTargetRoot));
        StartSortingCommand.NotifyCanExecuteChanged();
        ClearRunResultBanner();
        if (_isInitializing) return;
        _ = RecomputePreviewAsync();
    }

    partial void OnCustomTargetFolderChanged(string? value)
    {
        OnPropertyChanged(nameof(EffectiveTargetRoot));
        StartSortingCommand.NotifyCanExecuteChanged();
        ClearRunResultBanner();
        if (_isInitializing) return;
        _ = RecomputePreviewAsync();
    }

    private void ClearRunResultBanner()
    {
        StatusMessage = null;
        _statusMessageIsWarning = false;
    }

    partial void OnTransferCountChanged(int value) => StartSortingCommand.NotifyCanExecuteChanged();

    partial void OnHasEnoughSpaceChanged(bool value) => StartSortingCommand.NotifyCanExecuteChanged();

    #endregion

    #region Commands

    /// <summary>Loads the enabled LoRA sources (favorite preselected, else first) and computes the initial preview.
    /// Called from the view's <c>OnAttachedToVisualTree</c>.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            ClearRunResultBanner();
            _isInitializing = true;
            try
            {
                if (_settingsService is not null)
                {
                    var sources = await _settingsService.GetEnabledLoraSourcesAsync();
                    SourceFolders.Clear();
                    foreach (var source in sources)
                        SourceFolders.Add(source);

                    var favorite = await _settingsService.GetFavoriteLoraSourceAsync();
                    SelectedSourceFolder = favorite is not null && SourceFolders.Contains(favorite, StringComparer.OrdinalIgnoreCase)
                        ? favorite
                        : SourceFolders.FirstOrDefault();
                }
            }
            finally
            {
                _isInitializing = false;
            }

            // Initialize always re-enumerates: a prior run may have moved files since this VM was last used.
            InvalidateCandidateCache();
            await RecomputePreviewAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A blank tab with an unobserved task exception is worse than a visible failure —
            // e.g. a DB error resolving enabled sources, or an access-denied source folder.
            _isInitializing = false;
            _logger?.Error(LogCategory.FileSystem, LogSource, $"Preview failed: {ex.Message}", ex);
            StatusMessage = $"Preview failed: {ex.Message}";
            _statusMessageIsWarning = false;
            DisarmPlan($"Preview failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task BrowseSourceAsync()
    {
        if (DialogService is null) return;

        var folder = await DialogService.ShowOpenFolderDialogAsync("Select folder to sort");
        if (folder is null) return;

        if (!SourceFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            SourceFolders.Add(folder);

        SelectedSourceFolder = folder;
    }

    [RelayCommand]
    private async Task BrowseTargetAsync()
    {
        if (DialogService is null) return;

        var folder = await DialogService.ShowOpenFolderDialogAsync("Select target folder");
        if (folder is null) return;

        CustomTargetFolder = folder;
    }

    [RelayCommand]
    private void ClearTargetOverride() => CustomTargetFolder = null;

    [RelayCommand]
    private async Task RecomputePreviewAsync()
    {
        if (SelectedSourceFolder is null)
        {
            _resolveCts?.Cancel();
            // _lastPlan goes with the rest: leaving it behind is the same stale-armed-Start trap
            // DisarmPlan exists for.
            _lastPlan = null;
            PreviewRoots.Clear();
            PreviewSummary = null;
            DiskSummary = null;
            HasEnoughSpace = false;
            BlockReason = null;
            StatusMessage = null;
            _statusMessageIsWarning = false;
            TransferCount = 0;
            return;
        }

        await RunPreviewBusyAsync(RecomputePreviewCoreAsync, "Computing preview…");
    }

    /// <summary>
    /// Per-pass busy tracking for previews. The inherited <c>RunBusyAsync</c> cannot be used here:
    /// its <c>finally</c> unconditionally clears <see cref="IsBusy"/>, so with two overlapping
    /// passes the first one to finish re-enabled Start while the other was still computing.
    /// The overlay drops only when the last pass leaves; the message always belongs to the
    /// newest one.
    /// </summary>
    private async Task RunPreviewBusyAsync(Func<Task> action, string? message)
    {
        Interlocked.Increment(ref _inFlightPreviews);
        IsBusy = true;
        BusyMessage = message;
        try
        {
            await action();
        }
        finally
        {
            if (Interlocked.Decrement(ref _inFlightPreviews) == 0)
            {
                IsBusy = false;
                BusyMessage = null;
            }
        }
    }

    private async Task RecomputePreviewCoreAsync()
    {
        var sourceFolder = SelectedSourceFolder!;
        var targetRoot = EffectiveTargetRoot!;

        // Claim ownership BEFORE cancelling the previous pass, so the pass being superseded can
        // never observe itself as current on its way out.
        var generation = Interlocked.Increment(ref _previewGeneration);

        // Whether this pass still owns the VM's state. Checked before committing anything, on
        // every exit path — cancelled, failed or successful.
        bool IsCurrentPass() => generation == Volatile.Read(ref _previewGeneration);

        // Cancel any in-flight pass unconditionally — even a cache-hit pass for a different
        // selection must stop a stale resolve for a previously-selected source, otherwise it can
        // complete later and its continuation (below) clobbers what this pass is about to commit.
        // Cancel only: each pass disposes its own CTS in its finally, once its awaited work has
        // actually finished. Disposing the *previous* pass's source from here would race a resolve
        // that is still running into an ObjectDisposedException the moment it next handed the token
        // to HttpClient — and _resolveCts is null whenever no pass is in flight, so Cancel() can
        // never land on a disposed source either.
        _resolveCts?.Cancel();

        // One CTS per pass, created even on a cache hit. Planning re-hashes on every collision and
        // every option toggle re-plans, so it can run for minutes on a large library — and the
        // overlay's Cancel button used to be a complete no-op for that whole phase, because this
        // field was only populated on a cache miss.
        var passCts = new CancellationTokenSource();
        _resolveCts = passCts;

        // Each new pass starts with a clean flag — CancelSort only sets it for the pass currently
        // in flight, so a leftover true here (from a resolve that ran to completion despite being
        // cancelled, racing past the OperationCanceledException) must not mislabel a later silent
        // supersede as a user cancel.
        _previewCancelledByUser = false;

        try
        {
            List<SortCandidate> candidates;
            if (_candidateCache is not null && string.Equals(_candidateCacheSourceFolder, sourceFolder, StringComparison.OrdinalIgnoreCase))
            {
                candidates = _candidateCache;
            }
            else
            {
                // Candidate resolution touches disk (enumeration + per-file metadata/hashing) and
                // must not run on the UI thread — a browsed folder with thousands of unknown files
                // would freeze the overlay. ResolveCandidatesAsync used to set BusyMessage directly;
                // it now reports through this UI-thread-constructed IProgress<string> instead, whose
                // Report() marshals back via the captured SynchronizationContext.
                // Only the newest pass owns the overlay text — otherwise a superseded pass's
                // trailing "Resolving metadata 40/900…" reports overwrite the live one's.
                var resolveProgress = new Progress<string>(msg =>
                {
                    if (IsCurrentPass()) BusyMessage = msg;
                });

                var resolution = await Task.Run(
                    () => ResolveCandidatesAsync(sourceFolder, resolveProgress, passCts.Token),
                    passCts.Token);

                // Commit guard: the awaited resolve may have let a newer recompute pass start
                // (a changed source/target, or a toggled option). Ownership subsumes comparing
                // the individual inputs — every one of them starts a new pass, and options such
                // as IsMove were never part of the old field-by-field comparison.
                if (!IsCurrentPass()) return;

                candidates = resolution.Candidates;
                _candidateCache = candidates;
                _candidateCacheSourceFolder = sourceFolder;
                _candidateCacheSkippedCount = resolution.SkippedCount;
            }

            // BuildPlan also touches disk (lazy hashing on collisions), so it's offloaded too. The
            // options snapshot is read here — on the calling (UI) context — not from inside the
            // Task.Run body.
            var options = BuildOptions();
            var planStopwatch = Stopwatch.StartNew();
            var plan = await Task.Run(
                () => new LoraSortPlanner(_hashFile, _fileExistsOnDisk).BuildPlan(candidates, options, passCts.Token),
                passCts.Token);
            planStopwatch.Stop();

            // Second commit guard, for the same reason: planning can run for minutes on a large
            // library, and a cancelled token does not stop a delegate that is already running —
            // so a superseded pass reaches this line with a complete, stale plan.
            if (!IsCurrentPass()) return;

            _lastPlan = plan;

            BuildPreviewTree(plan, targetRoot);

            TransferCount = plan.TransferCount;
            PreviewSummary = $"✓ {plan.TransferCount} files will {(IsMove ? "move" : "copy")}   ·   {plan.AlreadyInPlaceCount} already in place   ·   ✎ {plan.RenamedCount} auto-renamed · {plan.SkippedDuplicateCount} duplicates skipped";

            ApplyDiskPreflight(plan, targetRoot);

            // Only clear a stale preview warning — a sort-run result message ("Done: …"/"Cancelled — …")
            // set by StartSortingAsync after its post-run recompute must survive this pass.
            if (_statusMessageIsWarning)
            {
                StatusMessage = null;
                _statusMessageIsWarning = false;
            }

            if (!IsMove && string.Equals(targetRoot, sourceFolder, StringComparison.OrdinalIgnoreCase))
            {
                HasEnoughSpace = false;
                BlockReason = "Copy into the source folder would duplicate every file — pick a different target.";
            }
            else if (SourceFolders.Any(s => !string.Equals(s, sourceFolder, StringComparison.OrdinalIgnoreCase) && IsWithin(targetRoot, s)))
            {
                StatusMessage = "⚠ Target is another LoRA source — colliding sources can lead to unpredictable outcomes (duplicate imports on the next scan).";
                _statusMessageIsWarning = true;
            }

            // Files the resolver could not read are silently absent from the preview otherwise, and
            // "the preview is short by 3 files" is exactly the kind of thing a user must be told.
            if (_candidateCacheSkippedCount > 0)
            {
                var note = $"⚠ {_candidateCacheSkippedCount} file(s) skipped (locked/unreadable) — see the log.";
                StatusMessage = _statusMessageIsWarning && StatusMessage is not null
                    ? $"{StatusMessage}   {note}"
                    : note;
                _statusMessageIsWarning = true;
            }

            _logger?.Info(LogCategory.FileSystem, LogSource,
                $"Plan built for {candidates.Count} candidates in {planStopwatch.ElapsedMilliseconds} ms: " +
                $"{plan.TransferCount} transfers, {plan.AlreadyInPlaceCount} already in place, " +
                $"{plan.RenamedCount} renames, {plan.SkippedDuplicateCount} duplicates, " +
                $"{FileSizeFormatter.Format(plan.RequiredBytes)} required");
        }
        catch (OperationCanceledException)
        {
            // Two callers cancel this pass: a newer recompute superseding it (silent — the newer
            // pass owns the state and will paint its own result), or the user hitting Cancel on the
            // busy overlay. _previewCancelledByUser tells them apart.
            if (!IsCurrentPass()) return;

            DisarmPlan("Preview was cancelled — press Refresh to rebuild it.");
            if (_previewCancelledByUser)
            {
                _previewCancelledByUser = false;
                StatusMessage = "Cancelled — preview not updated.";
                _statusMessageIsWarning = false;
            }
        }
        catch (Exception ex)
        {
            _logger?.Error(LogCategory.FileSystem, LogSource, $"Preview failed: {ex.Message}", ex);
            // Logged either way, but only painted when this pass still owns the state — a superseded
            // pass that dies on its way out must not overwrite the newer pass's result with an error.
            if (IsCurrentPass())
            {
                StatusMessage = $"Preview failed: {ex.Message}";
                _statusMessageIsWarning = false;
                DisarmPlan($"Preview failed: {ex.Message}");
            }
        }
        finally
        {
            if (ReferenceEquals(_resolveCts, passCts))
                _resolveCts = null;
            passCts.Dispose();
        }
    }

    /// <summary>
    /// Drops everything Start depends on. Previews wrote their results only on the success path, so
    /// a failed or cancelled pass left the button armed against the <i>previous</i> folder's plan:
    /// preview S1 succeeds (42 files, move), the user switches to S2 and that pass throws, the
    /// button still reads "Start Sorting (42 files)", and the confirm dialog interpolates the
    /// <i>live</i> IsMove/EffectiveTargetRoot ("42 files will be copied into S2") while
    /// <see cref="_lastPlan"/> still describes moving files inside S1. The
    /// <c>ReferenceEquals(_lastPlan, plan)</c> guard in <see cref="StartSortingAsync"/> was written
    /// for this class of problem but cannot see it, because nothing reassigned <c>_lastPlan</c>.
    /// </summary>
    private void DisarmPlan(string reason)
    {
        _lastPlan = null;
        TransferCount = 0;
        HasEnoughSpace = false;
        PreviewRoots.Clear();
        PreviewSummary = null;
        DiskSummary = null;
        BlockReason = reason;
    }

    /// <summary>Enumerates DB-known candidates under <paramref name="sourceFolder"/> plus
    /// unknown on-disk files, resolving the latter's metadata. Only invoked when the candidate
    /// cache misses (source-folder change or <see cref="InitializeAsync"/>) — option toggles
    /// reuse the cached result and skip this entirely (spec: preview recompute is in-memory).</summary>
    /// <returns>The candidates, plus the number of files skipped because they could not be read —
    /// see <see cref="IsSkippableFileFailure"/>.</returns>
    private async Task<CandidateResolution> ResolveCandidatesAsync(string sourceFolder, IProgress<string>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // The resolver memoizes the Civitai API key for the whole pass (it used to open a DI scope,
        // a DbContext and a query per file). Invalidate it once per pass so a key the user just
        // saved in Settings is picked up instead of being stale for the resolver's lifetime.
        _metadataResolver.ResetApiKeyCache();

        IReadOnlyList<InstalledModelFile> cached = _loadCachedFiles is null
            ? Array.Empty<InstalledModelFile>()
            : await _loadCachedFiles(ct);

        ct.ThrowIfCancellationRequested();

        var stopwatch = Stopwatch.StartNew();
        var candidates = new List<SortCandidate>();
        var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        var knownAdded = 0;
        var knownSkipped = 0;
        var unknownAdded = 0;
        var unknownSkipped = 0;

        foreach (var f in cached)
        {
            var path = f.File.LocalPath;

            try
            {
                if (string.IsNullOrEmpty(path)) continue;

                // FIRST, before anything that can throw. This marks the path as "the DB loop has
                // taken responsibility for it", and the unknown-file walk below skips it on that
                // basis. Registering it only after the size/sidecar work meant a DB-known
                // .safetensors that failed to read — one held open by a running ComfyUI — was
                // counted as skipped AND then re-enumerated as unknown: a full-file SHA256 plus a
                // serialized Civitai round-trip on the same file, "2 file(s) skipped" reported for
                // one file if it failed again, and if it succeeded, a candidate built from API
                // metadata instead of its own DB row (losing UserCategory, the DB base model and
                // the stored hash). Registering a path that then turns out to be outside the source
                // or absent costs nothing: neither is ever enumerated.
                knownPaths.Add(Path.GetFullPath(path));

                // Boundary-aware: a bare StartsWith would sweep sibling folders that merely share a
                // name prefix (e.g. source "E:\Loras" matching "E:\Loras_backup\x.safetensors").
                // Path.GetFullPath (inside IsWithin) throws ArgumentException for a whitespace-only
                // path — the check lives inside this try so a single blank/malformed ModelFile.LocalPath
                // row is skipped instead of aborting the whole pass.
                if (!IsWithin(path, sourceFolder)) continue;
                if (!_fileExistsOnDisk(path)) continue;

                var category = SorterCategoryResolver.ToFolderName(SorterCategoryResolver.ResolveForModel(f.Model));
                var sizeBytes = f.File.FileSizeBytes ?? new FileInfo(path).Length;
                var candidate = new SortCandidate(path, f.Version.BaseModelRaw, category,
                    f.Version.CivitaiId, f.File.HashSHA256, sizeBytes, SidecarLocator.FindSidecars(path));
                candidates.Add(candidate);
                knownAdded++;
            }
            catch (Exception ex) when (IsSkippableFileFailure(ex))
            {
                skipped++;
                knownSkipped++;
                _logger?.Warn(LogCategory.FileSystem, LogSource, $"Skipping {path}: {ex.Message}");
            }
        }

        var unknownFiles = Directory.Exists(sourceFolder)
            ? EnumerateModelFilesSafe(sourceFolder)
                .Where(p => !knownPaths.Contains(Path.GetFullPath(p)))
                .ToList()
            : new List<string>();

        _logger?.Info(LogCategory.FileSystem, LogSource,
            $"Resolving candidates under {sourceFolder}: {candidates.Count} known, {unknownFiles.Count} unknown " +
            $"(enumeration {stopwatch.ElapsedMilliseconds} ms)");

        for (var i = 0; i < unknownFiles.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var path = unknownFiles[i];
            progress?.Report($"Resolving metadata {i + 1}/{unknownFiles.Count}…");

            // A heartbeat, so an exported log can tell "slow" (the line keeps advancing) from
            // "hung" (it stopped) — this loop is a full-file SHA256 plus a serialized Civitai
            // round-trip per file and is by far the slowest thing the feature does.
            if (i > 0 && i % ResolveLogInterval == 0)
            {
                _logger?.Info(LogCategory.FileSystem, LogSource,
                    $"Resolved {i}/{unknownFiles.Count} unknown files ({stopwatch.ElapsedMilliseconds} ms elapsed, {skipped} skipped)");
            }

            try
            {
                var metadata = await _metadataResolver.ResolveAsync(path, ct);
                var sizeBytes = new FileInfo(path).Length;
                // The resolver surfaces the sidecar's tags, so a properly downloaded LoRA in a browsed
                // folder lands in its real category folder. This used to hardcode Unknown, which meant
                // the headline "Browse any folder" feature dumped a fully-resolved library into
                // <Target>\<BaseModel>\Unknown\ — the spec reserves Unknown for genuinely unresolved
                // files. InferFolderName returns null when no tag names a category; the Unknown bucket
                // name is equivalent to null here (SorterPathBuilder.IsUnresolvedCategory treats both
                // as "no category segment"), so the candidate record stays non-null.
                var category = SorterCategoryResolver.InferFolderName(metadata.Tags)
                    ?? SorterPathBuilder.UnknownFolderName;
                candidates.Add(new SortCandidate(path, metadata.BaseModelRaw, category,
                    metadata.CivitaiVersionId, metadata.Sha256, sizeBytes, SidecarLocator.FindSidecars(path)));
                unknownAdded++;
            }
            catch (Exception ex) when (IsSkippableFileFailure(ex))
            {
                skipped++;
                unknownSkipped++;
                _logger?.Warn(LogCategory.FileSystem, LogSource, $"Skipping {path}: {ex.Message}");
            }
        }

        if (skipped > 0)
        {
            _logger?.Warn(LogCategory.FileSystem, LogSource,
                $"{skipped} file(s) under {sourceFolder} could not be read and were left out of the preview.");
        }

        _logger?.Info(LogCategory.FileSystem, LogSource,
            $"Candidate resolution finished: {candidates.Count} candidates " +
            $"({knownAdded} DB-known added, {knownSkipped} DB-known skipped, " +
            $"{unknownAdded} resolved from disk/API, {unknownSkipped} unknown skipped), " +
            $"{skipped} skipped, {stopwatch.ElapsedMilliseconds} ms");

        return new CandidateResolution(candidates, skipped);
    }

    /// <summary>Whether a per-file failure should cost one file rather than the whole preview.</summary>
    /// <remarks>
    /// A single unreadable file used to abort the entire pass: the hash read
    /// (<c>File.OpenRead</c> with <c>FileShare.Read</c>) throws for a <c>.safetensors</c> held open
    /// by a running ComfyUI/A1111 or behind a denied ACL, <c>new FileInfo(path).Length</c> throws
    /// <see cref="FileNotFoundException"/> when the file vanished between enumeration and use, and a
    /// Civitai response-shape change raises <see cref="JsonException"/> — this repo has been bitten
    /// by that twice. One bad file out of 3000 killed the preview with zero candidates, which is the
    /// folder-granularity failure the safe directory walk exists to prevent, one level down.
    /// <see cref="OperationCanceledException"/> is deliberately not covered: cancellation must
    /// still unwind the pass. <see cref="ArgumentException"/> is included because
    /// <see cref="Path.GetFullPath(string)"/> (via <see cref="IsWithin"/>) throws it for a
    /// whitespace-only path — a blank <c>ModelFile.LocalPath</c> row must cost one file, not the
    /// whole pass.
    /// </remarks>
    private static bool IsSkippableFileFailure(Exception ex)
        => ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException;

    /// <summary>Result of one resolution pass: the candidates and how many files it had to skip.</summary>
    private sealed record CandidateResolution(List<SortCandidate> Candidates, int SkippedCount);

    /// <summary>Search a root and every subfolder, skip locked/no-permission subtrees, and do not
    /// follow reparse points. The same shape the rest of the app already uses
    /// (<c>ImageResourceHasher.RecursiveSafe</c>, <c>PipelineAssetInstaller.RecursiveSafe</c>).</summary>
    /// <remarks>
    /// <c>AttributesToSkip</c> excludes <see cref="FileAttributes.ReparsePoint"/>, which is the cycle
    /// guard: this replaced a hand-rolled stack walk in which a directory junction pointing at itself
    /// or an ancestor (<c>mklink /J</c>, or the symlinked shared-models directory common in
    /// multi-backend setups) grew the pending stack without bound and the preview never returned.
    /// Junctions and symlinks are therefore not followed — a LoRA reachable only through one is not
    /// enumerated, which is the correct trade for a walk of an arbitrary user-picked folder.
    /// </remarks>
    private static readonly EnumerationOptions RecursiveSafe = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        MatchCasing = MatchCasing.CaseInsensitive,
        // ReparsePoint ONLY. The default is Hidden | System, and EnumerationOptions applies
        // AttributesToSkip to files as well as directories: a .safetensors carrying the System
        // bit — common on files restored by a backup tool, copied off a NAS, or under a folder a
        // sync client marked System — simply never appeared in the preview, with no warning, no
        // log line, and a "N file(s) skipped" note still reading 0 because nothing threw. The
        // walk this replaced used Directory.GetFiles, which returns Hidden/System entries alike.
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    /// <summary>Enumerates model files under <paramref name="root"/>, tolerating an inaccessible or
    /// vanished subtree instead of aborting the whole preview — arbitrary browsed folders are a
    /// headline feature of the sorter, so both are expected to happen. Materialized as it goes, so a
    /// mid-walk failure keeps whatever was already found rather than discarding the pass.</summary>
    private List<string> EnumerateModelFilesSafe(string root)
    {
        var files = new List<string>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", RecursiveSafe))
            {
                if (ModelExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                    files.Add(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // DirectoryNotFoundException (an IOException) is the enumerate-while-it-is-being-moved
            // race; IgnoreInaccessible already absorbs per-subtree ACL denials.
            _logger?.Warn(LogCategory.FileSystem, LogSource,
                $"Directory walk of '{root}' stopped early after {files.Count} file(s): {ex.Message}");
        }
        return files;
    }

    [RelayCommand]
    private void CancelSort()
    {
        _sortCts?.Cancel();
        if (_resolveCts is not null)
        {
            _previewCancelledByUser = true;
            _resolveCts.Cancel();
        }
    }

    /// <summary>Manually re-syncs a stale preview: clears the resolved-candidate cache and
    /// re-enumerates disk from scratch, same as switching source folders would. Needed because
    /// nothing else invalidates the cache — files can change underneath a browsed folder (or an
    /// enabled source) without the VM ever finding out.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        ClearRunResultBanner();
        InvalidateCandidateCache();
        await RecomputePreviewAsync();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartSortingAsync()
    {
        var plan = _lastPlan;
        if (plan is null) return;

        var totalTransferBytes = plan.Moves.Where(m => m.Action == PlannedAction.Transfer).Sum(m => m.Candidate.FileSizeBytes);

        var confirmed = DialogService is null
            ? false
            : await DialogService.ShowConfirmAsync("Start sorting?",
                $"{plan.TransferCount} files will be {(IsMove ? "moved" : "copied")} into {EffectiveTargetRoot}.\n" +
                $"{plan.RenamedCount} will be renamed, {plan.SkippedDuplicateCount} duplicates skipped.\n" +
                $"Total {FileSizeFormatter.Format(totalTransferBytes)}.");

        if (!confirmed) return;

        // The preview may have changed while the confirm dialog was open (another recompute
        // landed a new plan). Starting against a stale plan reference would sort the wrong set.
        if (!ReferenceEquals(_lastPlan, plan))
        {
            StatusMessage = "Preview changed while confirming — check the updated preview and press Start again.";
            _statusMessageIsWarning = false;
            return;
        }

        try
        {
            _sortCts = new CancellationTokenSource();
            LoraSortResult result;
            try
            {
                result = await RunBusyAsync(() => ExecuteSortAsync(plan), "Sorting LoRAs…");
            }
            finally
            {
                _sortCts?.Dispose();
                _sortCts = null;
            }

            // Files moved/copied to new locations — the cached candidate list (built from their
            // old paths) is now stale and must be rebuilt from disk, not just re-planned.
            InvalidateCandidateCache();
            await RecomputePreviewAsync();

            // Set AFTER the post-run recompute so it isn't wiped by the recompute's warning-clear step.
            var message = result.Cancelled
                ? $"Cancelled — {result.Moved + result.Copied} done, rest untouched."
                : $"Done: {result.Moved + result.Copied} sorted, {result.Skipped} duplicates skipped, {result.Failed} failed.";
            if (_emptyFolderCleanupFailed)
                message += " (some empty folders could not be removed)";
            // A null manifest means the history file could not be written; the run itself is fine,
            // but there is no restore point for it, and silently pretending otherwise is worse.
            if (result.ManifestPath is null)
                message += " (no restore point was recorded — see the log)";
            StatusMessage = message;
            _statusMessageIsWarning = false;

            SortCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger?.Error(LogCategory.FileSystem, LogSource, $"Sort failed: {ex.Message}", ex);
            StatusMessage = $"Sorting failed: {ex.Message}";
            _statusMessageIsWarning = false;
        }
    }

    private async Task<LoraSortResult> ExecuteSortAsync(LoraSortPlan plan)
    {
        var taskTracker = App.Services?.GetService<ITaskTracker>();
        using var taskHandle = taskTracker?.BeginTask("Sorting LoRAs", LogCategory.FileSystem);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            _sortCts!.Token, taskHandle?.CancellationToken ?? CancellationToken.None);

        var executor = new LoraSortExecutor(_fileOperations, _pathUpdater,
            new SortHistoryWriter(_historyDirectory, _logger), _logger);
        var progress = new Progress<(double Fraction, string Status)>(p =>
        {
            BusyMessage = $"{p.Status} ({(int)(p.Fraction * 100)}%)";
            taskHandle?.ReportProgress(p.Fraction, p.Status);
        });

        var stopwatch = Stopwatch.StartNew();
        _logger?.Info(LogCategory.FileSystem, LogSource,
            $"Sort started: {(IsMove ? "move" : "copy")}, {plan.TransferCount} files " +
            $"({FileSizeFormatter.Format(plan.Moves.Where(m => m.Action == PlannedAction.Transfer).Sum(m => m.Candidate.FileSizeBytes))}) → {EffectiveTargetRoot}");

        LoraSortResult result;
        try
        {
            // ExecuteAsync is almost entirely synchronous file I/O and hashing — awaited directly it
            // would freeze the UI thread for the whole run (overlay never repaints, Cancel unclickable).
            // The executor touches no UI-bound state; `progress` was constructed above on the calling
            // (UI) context, so its Report() callbacks still marshal back correctly from the pool thread.
            result = await Task.Run(() => executor.ExecuteAsync(plan, progress, linkedCts.Token));
            taskHandle?.Complete($"{result.Moved + result.Copied} sorted, {result.Skipped} duplicates skipped, {result.Failed} failed.");
        }
        catch (Exception ex)
        {
            taskHandle?.Fail(ex, "Sort failed.");
            throw;
        }

        // Every file has already been transferred and every DB row repointed at this point, and
        // taskHandle.Complete() has already reported success. DiskUtility.DeleteEmptyDirectories
        // calls Directory.Delete with no guard of its own, so an Explorer window or AV holding one
        // now-empty folder — or a permission-denied subdirectory, exactly the case the directory
        // walk deliberately tolerates — used to unwind into StartSortingAsync's catch: the candidate
        // cache was never cleared, no post-run recompute happened, SortCompleted never fired so the
        // Installed tab never refreshed, and the user saw "Sorting failed: Access to the path … is
        // denied" over a preview still listing paths that no longer exist. Pressing Start then
        // replayed a plan whose sources were gone. A leftover empty folder is cosmetic; losing the
        // post-run bookkeeping is not.
        _emptyFolderCleanupFailed = false;
        if (IsMove && DeleteEmptySourceFolders && !result.Cancelled)
        {
            try
            {
                // The plan that actually ran, not the (possibly since-changed) live selection —
                // see the "preview changed while confirming" guard above this call's caller.
                await _deleteEmptyDirectories(plan.SourceRoot, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _emptyFolderCleanupFailed = true;
                _logger?.Warn(LogCategory.FileSystem, LogSource,
                    $"Sorted files moved fine, but empty source folders under {plan.SourceRoot} could not all be removed: {ex.Message}");
            }
        }

        _logger?.Info(LogCategory.FileSystem, LogSource,
            (result.Cancelled
                ? $"Sort cancelled: {result.Moved + result.Copied} done, {result.Skipped} skipped, {result.Failed} failed"
                : $"Sort finished: {result.Moved + result.Copied} sorted, {result.Skipped} duplicates skipped, {result.Failed} failed")
            + $" in {stopwatch.ElapsedMilliseconds} ms"
            + (result.ManifestPath is null ? " (no restore point recorded)" : $" (manifest: {result.ManifestPath})"));

        return result;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Free-space gate for the chosen target. Two things the naive version got wrong:
    /// <list type="bullet">
    /// <item><description>It assumed every target has a <see cref="DriveInfo"/>. Verified with a
    /// dotnet probe: <c>new DriveInfo(Path.GetPathRoot(@"\\nas\share\loras"))</c> throws
    /// <see cref="ArgumentException"/> ("Drive name must be a root directory"), and an unmapped
    /// letter throws <see cref="DriveNotFoundException"/> (an <see cref="IOException"/>). That
    /// escaped mid-pass, after the tree was painted but before the gate was set, leaving Start
    /// permanently disabled with no stated reason on a perfectly usable NAS target. The gate now
    /// fails <b>open</b> for that case — an existing folder whose free space is simply not
    /// knowable — and says so; the executor still reports per-file failures if the share really is
    /// full. An <i>unreachable</i> target (dead drive letter, denied or missing folder) is a
    /// different thing and still blocks: failing open there just moves the failure to the run,
    /// where it costs every file.</description></item>
    /// <item><description>It applied the 1 GB safety margin to a same-volume move, whose
    /// <see cref="LoraSortPlan.RequiredBytes"/> is 0 because the transfer is a directory-entry
    /// rename. That blocked the primary use case — reorganizing a library in place on the
    /// near-full drive it already lives on.</description></item>
    /// </list>
    /// </summary>
    private void ApplyDiskPreflight(LoraSortPlan plan, string targetRoot)
    {
        long free;
        try
        {
            free = _getAvailableSpace(targetRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Fail open only where there is genuinely no number to give: a target that has no
            // DriveInfo at all — new DriveInfo(@"\\nas\share\") throws ArgumentException — but
            // whose folder is right there. "Unknowable" and "unreachable" are different things.
            // A dead drive letter throws DriveNotFoundException (an IOException) and failing open
            // on it armed Start for a run the executor then failed on every single file:
            // "Done: 0 sorted, 0 duplicates skipped, 412 failed."
            if (ex is ArgumentException && Directory.Exists(targetRoot))
            {
                _logger?.Warn(LogCategory.FileSystem, LogSource,
                    $"Free space unavailable for '{targetRoot}' ({ex.GetType().Name}: {ex.Message}) — disk gate skipped.");
                HasEnoughSpace = true;
                DiskSummary = "Free space unknown (network or unsupported path) — proceed with care";
                BlockReason = null;
                return;
            }

            _logger?.Warn(LogCategory.FileSystem, LogSource,
                $"Target '{targetRoot}' could not be probed ({ex.GetType().Name}: {ex.Message}) — run blocked.");
            HasEnoughSpace = false;
            DiskSummary = "Free space unknown — the target could not be reached";
            BlockReason = "Target drive or folder is not reachable.";
            return;
        }

        var needed = plan.RequiredBytes > 0 ? plan.RequiredBytes + SafetyMarginBytes : 0;
        HasEnoughSpace = free >= needed;
        DiskSummary = $"{FileSizeFormatter.Format(plan.RequiredBytes)} required · {FileSizeFormatter.Format(free)} free";
        BlockReason = HasEnoughSpace ? null : "Not enough free space on the target drive.";
    }

    /// <summary>Drops the resolved-candidate cache so the next recompute re-enumerates disk and
    /// re-resolves metadata, together with everything derived from that pass.</summary>
    private void InvalidateCandidateCache()
    {
        _candidateCache = null;
        _candidateCacheSourceFolder = null;
        _candidateCacheSkippedCount = 0;
    }

    private LoraSortOptions BuildOptions() =>
        new(SelectedSourceFolder!, EffectiveTargetRoot!, IncludeCategory, IsMove, DeleteEmptySourceFolders);

    /// <summary>Walks each non-skipped move's TargetDirectory, strips the target-root prefix, and
    /// materializes nested folder nodes with rolled-up counts/sizes. Root nodes (first-level
    /// folders) are sorted by TotalBytes descending.</summary>
    private void BuildPreviewTree(LoraSortPlan plan, string targetRoot)
    {
        PreviewRoots.Clear();

        foreach (var move in plan.Moves)
        {
            if (move.Action == PlannedAction.SkippedDuplicate) continue;

            var relative = Path.GetRelativePath(targetRoot, move.TargetDirectory);
            var segments = relative == "."
                ? Array.Empty<string>()
                : relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

            var siblings = PreviewRoots;
            var chain = new List<SortPreviewNodeViewModel>();
            foreach (var segment in segments)
            {
                var node = GetOrCreateFolder(siblings, segment);
                chain.Add(node);
                siblings = node.Children;
            }

            var fileNode = new SortPreviewNodeViewModel
            {
                Name = Path.GetFileName(move.TargetFilePath),
                IsFile = true,
                TotalBytes = move.Candidate.FileSizeBytes,
                LoraCount = 1,
                IsAlreadyInPlace = move.Action == PlannedAction.AlreadyInPlace,
                IsRenamed = move.WasRenamed
            };
            siblings.Add(fileNode);

            foreach (var ancestor in chain)
            {
                ancestor.LoraCount += 1;
                ancestor.TotalBytes += move.Candidate.FileSizeBytes;
            }
        }

        var sortedRoots = PreviewRoots.OrderByDescending(n => n.TotalBytes).ToList();
        PreviewRoots.Clear();
        foreach (var root in sortedRoots)
            PreviewRoots.Add(root);
    }

    private static SortPreviewNodeViewModel GetOrCreateFolder(ObservableCollection<SortPreviewNodeViewModel> siblings, string name)
    {
        var existing = siblings.FirstOrDefault(n => !n.IsFile && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;

        var node = new SortPreviewNodeViewModel { Name = name, IsFile = false };
        siblings.Add(node);
        return node;
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> itself or nested beneath it.
    /// Boundary-aware — unlike a bare <c>StartsWith</c> this rejects a sibling folder that merely
    /// shares a name prefix (e.g. "E:\Loras" vs "E:\Loras_backup").</summary>
    /// <remarks>
    /// The root is normalized to end in exactly one separator rather than having its trailing one
    /// trimmed: <see cref="Path.TrimEndingDirectorySeparator(string)"/> deliberately does not trim a
    /// *root* path (its implementation is <c>EndsInDirectorySeparator(path) &amp;&amp; !IsRoot(path)</c>),
    /// so a drive-root source like <c>E:\</c> stayed 3 characters long and the boundary check then read
    /// the <c>L</c> of <c>E:\Loras\…</c> instead of a separator — making every path under a dedicated
    /// LoRA drive test as "outside" it. That discarded every DB-known file (full re-hash plus a Civitai
    /// round-trip each, and the whole library sorted into Unknown\) and silently disabled the
    /// "target is another LoRA source" warning. Verified with a probe: for <c>E:\</c> both
    /// <c>GetFullPath</c> and <c>TrimEndingDirectorySeparator</c> return <c>E:\</c>, length 3.
    /// </remarks>
    internal static bool IsWithin(string path, string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(path);

        // The root itself counts as "within", with or without a trailing separator on either side.
        if (string.Equals(Path.TrimEndingDirectorySeparator(fullPath),
                Path.TrimEndingDirectorySeparator(fullRoot), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootWithSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static Task DefaultDeleteEmptyDirectories(string path, CancellationToken ct)
        => new DiskUtility().DeleteEmptyDirectoriesAsync(path, ct);

    /// <summary>No-op path updater for the design-time constructor, which never touches the DB.</summary>
    private sealed class NullLocalPathUpdater : ILocalPathUpdater
    {
        public Task UpdateLocalPathsAsync(IReadOnlyList<(string OldPath, string NewPath)> changes, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    #endregion
}
