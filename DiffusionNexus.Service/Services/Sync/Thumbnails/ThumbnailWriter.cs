using DiffusionNexus.Domain.Entities;

namespace DiffusionNexus.Service.Services.Sync.Thumbnails;

/// <summary>
/// The one place the six thumbnail columns of a <see cref="ModelImage"/> are written (#521 Plan B).
/// </summary>
/// <remarks>
/// Every producer — the sync step, the tile's own repair path, the sidecar applier — goes through
/// here, because the columns are only meaningful as a set. A success that forgot to clear
/// <see cref="ModelImage.ThumbnailFailure"/> leaves yesterday's verdict sitting next to today's
/// bytes, and <see cref="Domain.Services.Sync.SyncRetryPolicy.IsThumbnailDue"/> reads that pair;
/// a failure that blanked the data would turn a CDN hiccup into a visibly emptier library. Two
/// static methods rather than a service: this is a pure entity mutation with no dependencies, and
/// the caller owns both the clock and the save.
/// </remarks>
public static class ThumbnailWriter
{
    /// <summary>
    /// Records bytes: the payload's four columns, the attempt, and — necessarily — the absence of a
    /// failure.
    /// </summary>
    /// <param name="image">The tracked row to fill in.</param>
    /// <param name="payload">What the provider produced.</param>
    /// <param name="now">The run's clock, so every row of one item carries the same stamp.</param>
    public static void ApplySuccess(ModelImage image, ThumbnailPayload payload, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(payload);

        image.ThumbnailData = payload.Data;
        image.ThumbnailMimeType = payload.MimeType;
        image.ThumbnailWidth = payload.Width;
        image.ThumbnailHeight = payload.Height;
        image.ThumbnailAttemptedAt = now;
        image.ThumbnailFailure = null;
    }

    /// <summary>
    /// Records that an attempt was made and what it concluded. Whatever the row already holds is
    /// left exactly as it is: those bytes are what the tile is currently showing, and a failed
    /// re-fetch is no reason to take them away.
    /// </summary>
    /// <param name="image">The tracked row to stamp.</param>
    /// <param name="reason">One of <see cref="ThumbnailFailureReason"/> — the retry policy reads it.</param>
    /// <param name="now">The run's clock.</param>
    public static void ApplyFailure(ModelImage image, string reason, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(image);
        // A null/empty reason would stamp "attempted, no failure" — which the retry policy reads
        // as success, freezing a byte-less row out of every future run. Refuse it loudly instead.
        ArgumentException.ThrowIfNullOrEmpty(reason);

        image.ThumbnailAttemptedAt = now;
        image.ThumbnailFailure = reason;
    }
}
