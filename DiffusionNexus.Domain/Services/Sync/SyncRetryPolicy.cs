using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Domain.Services.Sync;

/// <summary>Governs when a stale sync outcome is due to be re-checked.</summary>
public sealed record SyncRetryPolicy(TimeSpan NotIdentifiedRetryAfter, TimeSpan ErrorRetryAfter, int MaxErrorAttempts)
{
    public static SyncRetryPolicy Default { get; } = new(TimeSpan.FromDays(30), TimeSpan.FromDays(1), 3);

    /// <summary>
    /// Builds a policy from the user's saved settings. Clamps both windows to 1–3650 days:
    /// <see cref="MaxErrorAttempts"/> stays at the default, because it caps API hammering inside
    /// one run window and is not a user-facing knob.
    /// </summary>
    /// <remarks>
    /// The floor: zero would mean "always due", which turns every plan into a full re-run — when
    /// that is wanted it is a Force checkbox on the plan dialog, not a saved setting.
    /// <para>
    /// The cap: these values are not guaranteed to come from the settings combo boxes.
    /// <c>SettingsExportService.ImportAsync</c> copies them straight out of a JSON file with no
    /// range check, and <c>AppSettingsService.SaveSettingsAsync</c> persists them unvalidated —
    /// so a hand-edited or corrupted <c>settings.json</c> carrying <c>2147483647</c> reached
    /// <see cref="TimeSpan.FromDays"/>, which throws above ~10 675 199 days. Every Download
    /// Metadata press then died with "TimeSpan overflowed because the duration is too long", and
    /// the tiles' startup read silently fell back to the default: an unusable button with no way
    /// to see why. Ten years is past every meaningful horizon, so clamping degrades instead of
    /// throwing — the same "the service owns the sane range" stance already applied to
    /// <c>ThumbnailConcurrency</c> in <c>LibrarySyncService</c>.
    /// </para>
    /// </remarks>
    public static SyncRetryPolicy FromDays(int notIdentifiedDays, int errorDays) =>
        new(TimeSpan.FromDays(Math.Clamp(notIdentifiedDays, 1, 3650)),
            TimeSpan.FromDays(Math.Clamp(errorDays, 1, 3650)),
            Default.MaxErrorAttempts);

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

    /// <summary>
    /// Whether one image's thumbnail is due, given what the last attempt on that row recorded.
    /// </summary>
    /// <remarks>
    /// Per <i>image</i>, not per model: an image that 404s must not keep a whole library's sync
    /// coming back to it, and one that timed out must not be written off forever. The reasons split
    /// three ways. <see cref="ThumbnailFailureReason.IsHardFailure"/> ones are final answers — the
    /// URL is gone, the bytes are not an image, the file is not there, the scheme is unfetchable —
    /// and retrying them costs a request to learn nothing, so only an explicit force does.
    /// Everything else is transient and comes back after <see cref="ErrorRetryAfter"/>. There is no
    /// attempt counter to exhaust the way <see cref="IsIdentifyDue"/> has: a soft thumbnail failure
    /// re-attempts on a fixed cadence for as long as it keeps failing, because the CDN coming back
    /// is exactly the case that has to be caught.
    /// <para>
    /// <see cref="ThumbnailFailureReason.Corrupt"/> is the one reason that is due immediately: it
    /// is not a fetch that failed but an existing BLOB found unreadable and nulled, so the row is
    /// currently thumbnail-less through no fault of the source and the next run should just fetch it.
    /// </para>
    /// <para>
    /// A row that succeeded (<paramref name="failure"/> null with an attempt recorded) is never due
    /// without a force. It would not be offered anyway — selection drops images that already have
    /// bytes — so this only matters for the one that succeeded and then lost its BLOB, which is the
    /// Corrupt case above.
    /// </para>
    /// </remarks>
    /// <param name="attemptedAt">When the pipeline last tried this image, or null if it never has.</param>
    /// <param name="failure">The reason the last attempt recorded — one of <see cref="ThumbnailFailureReason"/> — or null after a success.</param>
    /// <param name="now">The run's clock.</param>
    /// <param name="force">The user asked for this row specifically; every rule above yields.</param>
    public bool IsThumbnailDue(DateTimeOffset? attemptedAt, string? failure, DateTimeOffset now, bool force)
        => force
           || attemptedAt is null
           || failure == ThumbnailFailureReason.Corrupt
           || (failure is not null
               && !ThumbnailFailureReason.IsHardFailure(failure)
               && now - attemptedAt.Value >= ErrorRetryAfter);
}
