namespace DiffusionNexus.Domain.Services.Sync;

/// <summary>Which steps a sync run performs, and whether previously-checked items are forced to re-run.</summary>
public sealed record SyncOptions(
    IReadOnlySet<SyncStepKind> Steps,          // which steps to run
    bool ForceIdentify = false,                // re-run identity even when NotIdentified/Matched-by-fallback
    bool ForceTags = false,
    bool ForceImages = false,
    bool ForceThumbnails = false,
    SyncRetryPolicy? RetryPolicy = null)
{
    public static SyncOptions All { get; } = new(new HashSet<SyncStepKind>(Enum.GetValues<SyncStepKind>()));
    public SyncRetryPolicy Policy => RetryPolicy ?? SyncRetryPolicy.Default;
}
