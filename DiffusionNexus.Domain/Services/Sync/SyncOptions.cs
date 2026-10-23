namespace DiffusionNexus.Domain.Services.Sync;

/// <summary>Which steps a sync run performs, and whether previously-checked items are forced to re-run.</summary>
/// <param name="ThumbnailConcurrency">
/// Thumbnails-step download parallelism, clamped to 1–8 by the service. API steps ignore it.
/// </param>
public sealed record SyncOptions(
    IReadOnlySet<SyncStepKind> Steps,          // which steps to run
    bool ForceIdentify = false,                // re-run identity even when NotIdentified/Matched-by-fallback
    bool ForceTags = false,
    bool ForceImages = false,
    bool ForceThumbnails = false,
    SyncRetryPolicy? RetryPolicy = null,
    int ThumbnailConcurrency = 4)
{
    public static SyncOptions All { get; } = new(new HashSet<SyncStepKind>(Enum.GetValues<SyncStepKind>()));
    public SyncRetryPolicy Policy => RetryPolicy ?? SyncRetryPolicy.Default;

    /// <summary>
    /// Every step kind but one — derived from the enum, so a sixth member joins both surfaces that
    /// use it (the bulk button's plan and the plan dialog's rows) without anyone remembering to.
    /// The hand-written four-element copies this replaces would have left a new step silently
    /// absent from both, with no compile error and no failing test.
    /// </summary>
    public static IReadOnlySet<SyncStepKind> AllStepsExcept(SyncStepKind excluded)
        => Enum.GetValues<SyncStepKind>().Where(k => k != excluded).ToHashSet();
}
