using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Domain.Services.Sync;

/// <summary>Governs when a stale sync outcome is due to be re-checked.</summary>
public sealed record SyncRetryPolicy(TimeSpan NotIdentifiedRetryAfter, TimeSpan ErrorRetryAfter, int MaxErrorAttempts)
{
    public static SyncRetryPolicy Default { get; } = new(TimeSpan.FromDays(30), TimeSpan.FromDays(1), 3);

    /// <summary>Whether an identity attempt is due given the stored outcome.</summary>
    public bool IsIdentifyDue(SyncOutcome outcome, DateTimeOffset? checkedAt, int attempts, DateTimeOffset now, bool force)
    {
        if (force) return true;
        if (checkedAt is null || outcome == SyncOutcome.None) return true;
        return outcome switch
        {
            SyncOutcome.Matched => false,
            SyncOutcome.Error => attempts < MaxErrorAttempts && now - checkedAt.Value >= ErrorRetryAfter,
            _ => now - checkedAt.Value >= NotIdentifiedRetryAfter,   // Sidecar, Header, Heuristic, NotIdentified: a better source may appear
        };
    }

    /// <summary>Whether a "fetch once" step (tags/images) is due.</summary>
    public bool IsFetchDue(DateTimeOffset? checkedAt, bool force) => force || checkedAt is null;
}
