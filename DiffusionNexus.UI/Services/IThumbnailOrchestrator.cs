using Avalonia.Media.Imaging;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Priority levels for thumbnail loading requests.
/// Higher values are processed first by the orchestrator.
/// </summary>
public enum ThumbnailPriority
{
    /// <summary>Preload/prefetch for views not yet visible. Cancelled first when owner changes.</summary>
    Low = 0,

    /// <summary>Background view that is loaded but not active. Processed after Critical.</summary>
    Normal = 1,

    /// <summary>Active/visible view. Processed first and never auto-cancelled.</summary>
    Critical = 2
}

/// <summary>
/// Identifies the owner of a thumbnail request (a view, tab, or module).
/// Each view creates a single token and reuses it for all its requests.
/// Uses reference equality so each instance is unique.
/// </summary>
public sealed class ThumbnailOwnerToken
{
    /// <summary>
    /// Human-readable name for diagnostics and logging.
    /// </summary>
    public string Name { get; }

    public ThumbnailOwnerToken(string name)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }

    public override string ToString() => Name;
}

/// <summary>
/// Orchestrates thumbnail loading across multiple views with priority-based scheduling.
/// <para>
/// When the user switches views/tabs, the orchestrator promotes the new view's
/// pending requests to <see cref="ThumbnailPriority.Critical"/> and cancels or
/// deprioritizes the previous view's in-flight requests. The underlying
/// <see cref="IThumbnailService"/> LRU cache is still used for instant cache hits.
/// </para>
/// <para>
/// <b>Usage pattern:</b>
/// <list type="number">
/// <item>Each ViewModel creates a <see cref="ThumbnailOwnerToken"/> once.</item>
/// <item>Thumbnail property getters call <see cref="TryGetCached"/> first (fast path).</item>
/// <item>On cache miss, call <see cref="RequestThumbnailAsync"/> with the owner token.</item>
/// <item>When the view becomes active, the host calls <see cref="SetActiveOwner"/>.</item>
/// <item>When the view deactivates, <see cref="CancelRequests"/> is called for cleanup.</item>
/// </list>
/// </para>
/// <para>
/// <b>TODO: Linux Implementation</b> — Verify that priority thread scheduling
/// behaves identically on Linux; test under Wayland and X11 compositors.
/// </para>
/// </summary>
public interface IThumbnailOrchestrator
{
    /// <summary>
    /// Requests a thumbnail with priority-based scheduling.
    /// Returns from cache immediately if available; otherwise queues the request.
    /// The effective priority may be boosted to <see cref="ThumbnailPriority.Critical"/>
    /// if <paramref name="owner"/> is the current active owner.
    /// </summary>
    /// <param name="imagePath">Path to the image file.</param>
    /// <param name="owner">Token identifying the requesting view.</param>
    /// <param name="priority">Base priority for this request.</param>
    /// <param name="targetWidth">Target width for the thumbnail (default 340px).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The loaded bitmap, or null if loading failed or was cancelled.</returns>
    Task<Bitmap?> RequestThumbnailAsync(
        string imagePath,
        ThumbnailOwnerToken owner,
        ThumbnailPriority priority = ThumbnailPriority.Normal,
        int targetWidth = 340,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tries to get a cached thumbnail synchronously (fast path).
    /// Delegates to the underlying <see cref="IThumbnailService"/> cache.
    /// </summary>
    /// <param name="imagePath">Path to the image file.</param>
    /// <param name="bitmap">The cached bitmap if found.</param>
    /// <returns>True if the thumbnail was in cache.</returns>
    bool TryGetCached(string imagePath, out Bitmap? bitmap);

    /// <summary>
    /// Sets the active owner. Requests from this owner are boosted to
    /// <see cref="ThumbnailPriority.Critical"/>. Pending requests from
    /// other owners are deprioritized or cancelled.
    /// </summary>
    /// <param name="owner">The owner token of the newly active view.</param>
    void SetActiveOwner(ThumbnailOwnerToken owner);

    /// <summary>
    /// Gets the currently active owner, or null if none is set.
    /// </summary>
    ThumbnailOwnerToken? ActiveOwner { get; }

    /// <summary>
    /// Cancels all pending (not yet started) requests for a specific owner.
    /// In-flight requests will be signalled via their CancellationToken.
    /// </summary>
    /// <param name="owner">The owner whose requests should be cancelled.</param>
    void CancelRequests(ThumbnailOwnerToken owner);

    /// <summary>
    /// Invalidates a specific cache entry (e.g., when the image is modified).
    /// Delegates to the underlying <see cref="IThumbnailService"/>.
    /// </summary>
    /// <param name="imagePath">Path to the image to invalidate.</param>
    void Invalidate(string imagePath);

    /// <summary>
    /// Clears all cached thumbnails.
    /// Delegates to the underlying <see cref="IThumbnailService"/>.
    /// </summary>
    void ClearCache();

    /// <summary>
    /// Gets the current cache statistics.
    /// </summary>
    ThumbnailCacheStats GetStats();
}
