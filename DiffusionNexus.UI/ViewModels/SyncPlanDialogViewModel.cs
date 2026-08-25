using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.Domain.Services.Sync;
using DiffusionNexus.Domain.Services.UnifiedLogging;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>The plan dialog's outcome. A cancelled dialog carries no options.</summary>
public sealed record SyncPlanDialogResult(bool Confirmed, SyncOptions? Options)
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
    internal const string UpToDateMessage = "Library is up to date — nothing to do";

    /// <summary>
    /// The rows, in fixed display order. DiscoverFiles is deliberately absent: discovery has
    /// already run by the time this dialog opens, so there is nothing left to decide about it.
    /// Re-plans always ask for all four — the ticks filter at Start, not at plan time.
    /// </summary>
    private static readonly SyncStepKind[] RowOrder =
    [
        SyncStepKind.IdentifyModel, SyncStepKind.FetchTags, SyncStepKind.FetchImages, SyncStepKind.Thumbnails,
    ];

    private static readonly IReadOnlySet<SyncStepKind> AllRowKinds = new HashSet<SyncStepKind>(RowOrder);

    private readonly SyncOptions _baseOptions;
    private readonly Func<SyncOptions, Task<SyncPlan>> _replanAsync;
    private readonly IUnifiedLogger? _logger;
    private Task _replanTask = Task.CompletedTask;

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

        Rows = RowOrder.Select(kind => new SyncPlanStepRowViewModel(kind, DescribeStep(initialPlan, kind))).ToList();
        foreach (var row in Rows)
        {
            row.SelectionChanged = RefreshDerived;
        }

        LastRunText = lastLibrarySyncAt is { } last
            ? $"Last full sync: {last.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}"
            : "Last full sync: never";

        HasDiscoveredFiles = newFilesDiscovered > 0;
        DiscoveredText = newFilesDiscovered switch
        {
            <= 0 => "",
            1 => "1 new file discovered",
            _ => $"{newFilesDiscovered} new files discovered",
        };

        ApplyPlan(initialPlan);
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
    /// <see cref="_replanTask"/> for the new tail before anything awaits is what makes
    /// <see cref="WhenReplanSettles"/> cover re-plans queued while another is running.
    /// </summary>
    private void QueueReplan()
    {
        var options = OptionsWith(AllRowKinds);
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
            IsReplanning = true;
            _logger?.Info(LogCategory.Network, "CivitaiSync",
                $"Plan dialog: re-planning with forces identify={options.ForceIdentify} tags={options.ForceTags} " +
                $"images={options.ForceImages} thumbnails={options.ForceThumbnails}");

            var plan = await _replanAsync(options);
            ApplyPlan(plan);
        }
        catch (Exception ex)
        {
            // Keep the counts we already have: a dialog that blanks itself because a query
            // hiccuped is worse than one showing a slightly stale plan.
            _logger?.Warn(LogCategory.Network, "CivitaiSync",
                $"Plan dialog: re-plan failed, keeping the previous counts: {ex.Message}");
        }
        finally
        {
            IsReplanning = false;
        }
    }

    private void ApplyPlan(SyncPlan plan)
    {
        foreach (var row in Rows)
        {
            var step = plan.Steps.FirstOrDefault(s => s.Kind == row.Kind);

            // Read before the count changes: it is the pre-plan enabled state that decides
            // whether this row's tick is the user's choice or ours to make.
            var wasEnabled = row.IsEnabled;

            row.Estimate = step?.EstimatedDuration ?? TimeSpan.Zero;
            row.Count = step?.Count ?? 0;
            row.EstimateText = FormatDuration(row.Estimate);

            // A row that had nothing to do could not have been ticked, so a force that gives it
            // work ticks it. A row that was live keeps whatever the user decided about it.
            if (!wasEnabled)
            {
                row.IsSelected = row.Count > 0;
            }
        }

        RefreshDerived();
    }

    private void RefreshDerived()
    {
        IsUpToDate = Rows.All(r => r.Count == 0);
        CanStart = !IsReplanning && Rows.Any(r => r.IsSelected && r.Count > 0);
        TotalEstimateText = FormatDuration(TimeSpan.FromTicks(Rows.Where(r => r.IsSelected).Sum(r => r.Estimate.Ticks)));

        OnPropertyChanged(nameof(IsUpToDate));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(TotalEstimateText));
    }

    public SyncPlanDialogResult BuildResult()
    {
        var steps = Rows.Where(r => r.IsSelected && r.Count > 0).Select(r => r.Kind).ToHashSet();
        return new SyncPlanDialogResult(true, OptionsWith(steps));
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

    /// <summary>"~45 s" under 90 s, "~4 min" under 90 min, else "~1.5 h".</summary>
    internal static string FormatDuration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;

        if (t.TotalSeconds < 90)
        {
            return $"~{Math.Round(t.TotalSeconds).ToString("0", CultureInfo.InvariantCulture)} s";
        }

        if (t.TotalMinutes < 90)
        {
            return $"~{Math.Round(t.TotalMinutes).ToString("0", CultureInfo.InvariantCulture)} min";
        }

        return $"~{t.TotalHours.ToString("0.#", CultureInfo.InvariantCulture)} h";
    }
}
