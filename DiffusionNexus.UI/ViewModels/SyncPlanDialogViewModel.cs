using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.Domain.Services.Sync;
using DiffusionNexus.Domain.Services.UnifiedLogging;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>The plan dialog's outcome. A cancelled dialog carries no options.</summary>
/// <param name="Plan">
/// The plan the dialog was showing when Start was pressed, filtered to the ticked kinds — the
/// caller may execute it instead of paying for a third full selection pass over the library. Null
/// when the dialog cannot vouch for its own counts (a re-plan failed and left them stale relative
/// to the force toggles), in which case the caller must plan again.
/// </param>
public sealed record SyncPlanDialogResult(bool Confirmed, SyncOptions? Options, SyncPlan? Plan = null)
{
    public static SyncPlanDialogResult Cancelled() => new(false, null);
}

/// <summary>One step row: what it would do, how many items, whether it runs.</summary>
public sealed partial class SyncPlanStepRowViewModel : ObservableObject
{
    public SyncPlanStepRowViewModel(SyncStepKind kind, string description)
    {
        Kind = kind;
        Description = description;
    }

    public SyncStepKind Kind { get; }
    public string Label => SyncReport.Label(Kind);
    public string Description { get; }

    [ObservableProperty] private int _count;
    [ObservableProperty] private string _estimateText = "";
    [ObservableProperty] private bool _isSelected;

    /// <summary>
    /// The planned duration behind <see cref="EstimateText"/>, kept so the dialog can total the
    /// selected rows without re-parsing the text it just formatted.
    /// </summary>
    internal TimeSpan Estimate { get; set; }

    /// <summary>A row with nothing to do cannot be ticked.</summary>
    public bool IsEnabled => Count > 0;
    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(IsEnabled));
    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();
    internal Action? SelectionChanged { get; set; }
}

/// <summary>
/// Confirmation dialog for a metadata sync run: the four candidate steps with their counts,
/// the force toggles that re-plan on the spot, and the options the run is started with.
/// </summary>
public sealed partial class SyncPlanDialogViewModel : ObservableObject
{
    /// <summary>The status bar behind this dialog says the same thing — from the same const (<see cref="SyncCopy.UpToDate"/>).</summary>
    internal const string UpToDateMessage = SyncCopy.UpToDate;

    /// <summary>
    /// The rows, in display order. DiscoverFiles is deliberately absent: discovery has already run
    /// by the time this dialog opens, so there is nothing left to decide about it. Re-plans always
    /// ask for every row kind — the ticks filter at Start, not at plan time.
    /// </summary>
    /// <remarks>
    /// Derived from the enum rather than listed: the enum's declaration order IS the display order
    /// (identify, then tags, then images, then thumbnails — the order the pipeline runs them in),
    /// so a fifth step appears here on its own. The hand-written list this replaces had to be kept
    /// in step with the enum and with the viewer's own copy, and nothing would have said otherwise.
    /// </remarks>
    private static readonly SyncStepKind[] RowOrder =
        Enum.GetValues<SyncStepKind>().Where(k => k != SyncStepKind.DiscoverFiles).ToArray();

    private static readonly IReadOnlySet<SyncStepKind> AllRowKinds = new HashSet<SyncStepKind>(RowOrder);

    private readonly SyncOptions _baseOptions;
    private readonly Func<SyncOptions, Task<SyncPlan>> _replanAsync;
    private readonly IUnifiedLogger? _logger;
    private Task _replanTask = Task.CompletedTask;

    /// <summary>
    /// How many re-plans are queued or in flight. Raised in <see cref="QueueReplan"/> at toggle
    /// time and lowered in <see cref="ReplanAfterAsync"/>'s finally; only the last one out lowers
    /// <see cref="IsReplanning"/>, so the flag covers the whole chain and not just one link of it.
    /// </summary>
    /// <remarks>
    /// No <c>Interlocked</c>: every touch is on the UI context. The toggles arrive through property
    /// setters raised by the dispatcher, and the continuations that decrement resume on the same
    /// context. It reads like a race and is not one.
    /// </remarks>
    private int _pendingReplans;

    /// <summary>The last plan that was actually applied to the rows — what the user is looking at.</summary>
    private SyncPlan _lastPlan;

    /// <summary>
    /// The force toggles <see cref="_lastPlan"/> was computed with. A failed re-plan updates
    /// neither: the counts on screen are then the previous plan's, and saying otherwise would hand
    /// the caller a plan for a different item set than the toggles now describe.
    /// </summary>
    private (bool Identify, bool Tags, bool Images, bool Thumbnails) _lastPlanForces;

    public SyncPlanDialogViewModel(
        SyncPlan initialPlan,
        SyncOptions baseOptions,
        Func<SyncOptions, Task<SyncPlan>> replanAsync,
        DateTimeOffset? lastLibrarySyncAt,
        int newFilesDiscovered,
        IUnifiedLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(initialPlan);
        ArgumentNullException.ThrowIfNull(baseOptions);
        ArgumentNullException.ThrowIfNull(replanAsync);

        _baseOptions = baseOptions;
        _replanAsync = replanAsync;
        _logger = logger;
        _lastPlan = initialPlan;

        Rows = RowOrder.Select(kind => new SyncPlanStepRowViewModel(kind, DescribeStep(initialPlan, kind))).ToList();
        foreach (var row in Rows)
        {
            row.SelectionChanged = RefreshDerived;
        }

        LastRunText = lastLibrarySyncAt is { } last
            ? $"Last full sync: {last.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}"
            : "Last full sync: never";

        HasDiscoveredFiles = newFilesDiscovered > 0;
        DiscoveredText = SyncCopy.DescribeDiscovered(newFilesDiscovered);

        // The initial plan counts as applied: its forces are all false — which is exactly the state
        // the toggles start in — and it was built over all four kinds, so it can be filtered down
        // to whatever the user ends up ticking.
        ApplyPlan(initialPlan, baseOptions);
    }

    public IReadOnlyList<SyncPlanStepRowViewModel> Rows { get; }

    [ObservableProperty] private bool _forceIdentify;      // "Re-check models not found on Civitai"
    [ObservableProperty] private bool _forceTags;
    [ObservableProperty] private bool _forceImages;
    [ObservableProperty] private bool _forceThumbnails;
    [ObservableProperty] private bool _isReplanning;

    /// <summary>True when no row has any work — the "nothing to do" state.</summary>
    public bool IsUpToDate { get; private set; }

    /// <summary>True when at least one ticked row has work and no re-plan is in flight.</summary>
    public bool CanStart { get; private set; }

    public string UpToDateText => UpToDateMessage;
    public string LastRunText { get; }
    public string DiscoveredText { get; }
    public bool HasDiscoveredFiles { get; }
    public string TotalEstimateText { get; private set; } = "";

    partial void OnForceIdentifyChanged(bool value) => QueueReplan();
    partial void OnForceTagsChanged(bool value) => QueueReplan();
    partial void OnForceImagesChanged(bool value) => QueueReplan();
    partial void OnForceThumbnailsChanged(bool value) => QueueReplan();
    partial void OnIsReplanningChanged(bool value) => RefreshDerived();

    /// <summary>
    /// Queues a re-plan behind whatever is already running. Serialized, not coalesced: the
    /// forces are snapshotted here, at toggle time, so each queued plan is the one that toggle
    /// asked for and the last one to land is the one the user last chose. Swapping
    /// <see cref="_replanTask"/> for the new tail before this method returns is what makes
    /// <see cref="WhenReplanSettles"/> cover re-plans queued while another is running — which
    /// means the prologue of <see cref="ReplanAfterAsync"/> before its first await must stay
    /// free of anything that can re-enter this method, or the inner queue link is silently
    /// dropped from the chain.
    /// <para>
    /// The flag goes up here, synchronously, at toggle time — not inside the queued work. Raised
    /// there, it came down when the first link finished and only went back up when the second
    /// link's continuation was pumped, leaving a dispatcher turn in which Start was live over the
    /// superseded counts: tick Force tags, tick Force image records, press Start in the gap, and
    /// the Images row was still 0 from the first plan, so FetchImages was silently dropped from
    /// the run the user had just asked for.
    /// </para>
    /// </summary>
    private void QueueReplan()
    {
        var options = OptionsWith(AllRowKinds);

        _pendingReplans++;
        IsReplanning = true;

        var previous = _replanTask;
        _replanTask = ReplanAfterAsync(previous, options);
    }

    /// <summary>
    /// A task that completes when every queued re-plan has been applied. Test seam: tests await
    /// it instead of polling for the new counts.
    /// </summary>
    internal Task WhenReplanSettles() => _replanTask;

    private async Task ReplanAfterAsync(Task previous, SyncOptions options)
    {
        // This method never faults — everything below is caught — so awaiting the predecessor
        // needs no guard of its own, and one failed re-plan can never strand the queue.
        await previous;

        try
        {
            _logger?.Info(LogCategory.Network, "CivitaiSync",
                $"Plan dialog: re-planning with forces identify={options.ForceIdentify} tags={options.ForceTags} " +
                $"images={options.ForceImages} thumbnails={options.ForceThumbnails}");

            var plan = await _replanAsync(options);
            ApplyPlan(plan, options);
        }
        catch (Exception ex)
        {
            // Keep the counts we already have: a dialog that blanks itself because a query
            // hiccuped is worse than one showing a slightly stale plan. Deliberately without
            // touching _lastPlan / _lastPlanForces — the counts are now the PREVIOUS plan's, and
            // BuildResult uses that mismatch to withhold a plan the caller must not run.
            _logger?.Warn(LogCategory.Network, "CivitaiSync",
                $"Plan dialog: re-plan failed, keeping the previous counts: {ex.Message}");
        }
        finally
        {
            // Only the last link out lowers it: with two toggles queued, the first one finishing
            // must not re-enable Start over counts the second is about to replace.
            if (--_pendingReplans == 0) IsReplanning = false;
        }
    }

    private void ApplyPlan(SyncPlan plan, SyncOptions builtWith)
    {
        foreach (var row in Rows)
        {
            var step = plan.Steps.FirstOrDefault(s => s.Kind == row.Kind);

            // Read before the count changes: it is the pre-plan enabled state that decides
            // whether this row's tick is the user's choice or ours to make.
            var wasEnabled = row.IsEnabled;

            row.Estimate = step?.EstimatedDuration ?? TimeSpan.Zero;
            row.Count = step?.Count ?? 0;
            // A row with nothing to do shows no estimate — "~0 s" reads like pending work.
            row.EstimateText = row.Count > 0 ? SyncCopy.FormatEstimate(row.Estimate) : "";

            // A row that had nothing to do could not have been ticked, so a force that gives it
            // work ticks it. A row that was live keeps whatever the user decided about it.
            // Known dip: an enabled row the user unticked that transiently drops to 0 and comes
            // back re-ticks itself — once it hit 0 the untick stopped being a live choice.
            if (!wasEnabled)
            {
                row.IsSelected = row.Count > 0;
            }
        }

        // Recorded together: the rows now show this plan's counts, and these are the forces that
        // produced them. BuildResult hands the plan on only while the two still agree.
        _lastPlan = plan;
        _lastPlanForces =
            (builtWith.ForceIdentify, builtWith.ForceTags, builtWith.ForceImages, builtWith.ForceThumbnails);

        RefreshDerived();
    }

    private void RefreshDerived()
    {
        IsUpToDate = Rows.All(r => r.Count == 0);
        CanStart = !IsReplanning && Rows.Any(r => r.IsSelected && r.Count > 0);
        // Same predicate as CanStart and BuildResult: a ticked-but-empty row is not part of the run,
        // so its (stale) estimate must not be part of the total either.
        TotalEstimateText = SyncCopy.FormatEstimate(TimeSpan.FromTicks(
            Rows.Where(r => r.IsSelected && r.Count > 0).Sum(r => r.Estimate.Ticks)));

        OnPropertyChanged(nameof(IsUpToDate));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(TotalEstimateText));
    }

    /// <summary>
    /// What the user chose, and — when the dialog can still vouch for its own numbers — the plan
    /// behind them, so the caller need not run a third full selection pass over the library.
    /// </summary>
    /// <remarks>
    /// The plan travels only while the current force toggles are the ones it was computed with.
    /// Every toggle re-plans, so that is the common case; the exception is a re-plan that failed
    /// and deliberately kept the previous counts, where the numbers now describe a different item
    /// set than the toggles do. It is filtered to the ticked kinds and re-labelled with the chosen
    /// options so <c>ExecuteAsync</c> runs exactly what was ticked, with the right forces.
    /// <para>
    /// Staleness is safe here: <c>RunStepAsync</c> re-selects per step at execution time, so the
    /// plan's counts only ever reach the report's Planned column. A dialog left open for minutes
    /// therefore costs a cosmetic number, not a wrong run.
    /// </para>
    /// </remarks>
    public SyncPlanDialogResult BuildResult()
    {
        var steps = Rows.Where(r => r.IsSelected && r.Count > 0).Select(r => r.Kind).ToHashSet();
        var options = OptionsWith(steps);

        var countsMatchTheToggles =
            _lastPlanForces == (ForceIdentify, ForceTags, ForceImages, ForceThumbnails);

        var plan = countsMatchTheToggles
            ? new SyncPlan(
                _lastPlan.Scope,
                options,
                _lastPlan.Steps.Where(s => steps.Contains(s.Kind)).ToList(),
                _lastPlan.PlannedAt)
            : null;

        return new SyncPlanDialogResult(true, options, plan);
    }

    /// <summary>
    /// The base options — which carry the retry policy and thumbnail concurrency the caller
    /// configured — with this dialog's steps and force toggles laid over them.
    /// </summary>
    private SyncOptions OptionsWith(IReadOnlySet<SyncStepKind> steps) => _baseOptions with
    {
        Steps = steps,
        ForceIdentify = ForceIdentify,
        ForceTags = ForceTags,
        ForceImages = ForceImages,
        ForceThumbnails = ForceThumbnails,
    };

    private static string DescribeStep(SyncPlan plan, SyncStepKind kind)
    {
        var description = plan.Steps.FirstOrDefault(s => s.Kind == kind)?.Description;
        return string.IsNullOrWhiteSpace(description) ? SyncReport.Label(kind) : description;
    }
}
