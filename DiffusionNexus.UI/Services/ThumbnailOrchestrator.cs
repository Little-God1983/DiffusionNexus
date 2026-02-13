using System.Collections.Concurrent;
using Avalonia.Media.Imaging;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Internal record representing a queued thumbnail request.
/// </summary>
internal sealed record ThumbnailRequest(
    string ImagePath,
    int TargetWidth,
    ThumbnailOwnerToken Owner,
    ThumbnailPriority Priority,
    TaskCompletionSource<Bitmap?> Completion,
    CancellationToken CancellationToken);

/// <summary>
/// Priority-based thumbnail loading orchestrator.
/// <para>
/// Wraps <see cref="IThumbnailService"/> and adds:
/// <list type="bullet">
/// <item>Priority queue — <see cref="ThumbnailPriority.Critical"/> requests are processed first.</item>
/// <item>Active-owner boosting — the active view's requests are auto-promoted.</item>
/// <item>Per-owner cancellation — switching views cancels the previous view's pending work.</item>
/// <item>Concurrency control — limits parallel decode operations.</item>
/// </list>
/// </para>
/// <para>
/// The orchestrator does <b>not</b> own the LRU cache; that remains in <see cref="IThumbnailService"/>.
/// Cache hits bypass the queue entirely for zero-latency returns.
/// </para>
/// <para>
/// <b>TODO: Linux Implementation</b> — Profile queue throughput under X11/Wayland to ensure
/// the priority processing loop does not starve the UI thread on single-core VMs.
/// </para>
/// </summary>
public sealed class ThumbnailOrchestrator : IThumbnailOrchestrator, IDisposable
{
    private readonly IThumbnailService _thumbnailService;
    private readonly PriorityQueue<ThumbnailRequest, int> _requestQueue = new();
    private readonly object _queueLock = new();
    private readonly ConcurrentDictionary<ThumbnailOwnerToken, CancellationTokenSource> _ownerCancellations = new();
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _inFlightRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _processingLoop;
    private ThumbnailOwnerToken? _activeOwner;
    private bool _disposed;

    /// <summary>
    /// Creates a new ThumbnailOrchestrator.
    /// </summary>
    /// <param name="thumbnailService">The underlying thumbnail service for loading and caching.
    /// The service owns concurrency throttling — the orchestrator does not add another semaphore.</param>
    public ThumbnailOrchestrator(IThumbnailService thumbnailService)
    {
        ArgumentNullException.ThrowIfNull(thumbnailService);
        _thumbnailService = thumbnailService;

        // Start the background processing loop
        _processingLoop = Task.Run(ProcessQueueAsync);
    }

    /// <inheritdoc />
    public ThumbnailOwnerToken? ActiveOwner => _activeOwner;

    /// <inheritdoc />
    public async Task<Bitmap?> RequestThumbnailAsync(
        string imagePath,
        ThumbnailOwnerToken owner,
        ThumbnailPriority priority = ThumbnailPriority.Normal,
        int targetWidth = 340,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(imagePath))
            return null;

        // Fast path: cache hit
        if (_thumbnailService.TryGetCached(imagePath, out var cached))
            return cached;

        // Deduplicate: if another request for the same path is already in-flight, piggyback on it
        if (_inFlightRequests.TryGetValue(imagePath, out var existingTask))
        {
            return await existingTask.ConfigureAwait(false);
        }

        // Boost priority if this owner is the active owner
        var effectivePriority = (owner == _activeOwner) ? ThumbnailPriority.Critical : priority;

        // Create a linked token that cancels when either the caller cancels or the owner is cancelled
        var ownerCts = GetOrCreateOwnerCts(owner);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, ownerCts.Token, _disposeCts.Token);

        var tcs = new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Register cancellation to complete the TCS
        linkedCts.Token.Register(() => tcs.TrySetResult(null), useSynchronizationContext: false);

        // Track the in-flight request for deduplication
        _inFlightRequests.TryAdd(imagePath, tcs.Task);

        // Remove from in-flight tracking once complete
        _ = tcs.Task.ContinueWith(
            _ => _inFlightRequests.TryRemove(imagePath, out _),
            TaskContinuationOptions.ExecuteSynchronously);

        var request = new ThumbnailRequest(
            imagePath, targetWidth, owner, effectivePriority, tcs, linkedCts.Token);

        Enqueue(request);

        return await tcs.Task.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public bool TryGetCached(string imagePath, out Bitmap? bitmap)
    {
        return _thumbnailService.TryGetCached(imagePath, out bitmap);
    }

    /// <inheritdoc />
    public void SetActiveOwner(ThumbnailOwnerToken owner)
    {
        // Simply update the active owner. New requests from this owner will be boosted
        // to Critical priority. We do NOT cancel the previous owner's in-flight work —
        // completed loads populate the cache which benefits the user when switching back.
        _activeOwner = owner;
    }

    /// <inheritdoc />
    public void CancelRequests(ThumbnailOwnerToken owner)
    {
        CancelOwnerCts(owner);
    }

    /// <inheritdoc />
    public void Invalidate(string imagePath)
    {
        _thumbnailService.Invalidate(imagePath);
    }

    /// <inheritdoc />
    public void ClearCache()
    {
        _thumbnailService.ClearCache();
    }

    /// <inheritdoc />
    public ThumbnailCacheStats GetStats()
    {
        return _thumbnailService.GetStats();
    }

    /// <summary>
    /// Background loop that dequeues requests in priority order and dispatches them.
    /// The semaphore is acquired inside each task, so the loop can keep dequeueing
    /// without blocking on concurrent load limits.
    /// </summary>
    private async Task ProcessQueueAsync()
    {
        while (!_disposeCts.IsCancellationRequested)
        {
            try
            {
                // Wait for work to arrive
                await _queueSignal.WaitAsync(_disposeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Dequeue the highest-priority request
            ThumbnailRequest? request;
            lock (_queueLock)
            {
                if (!_requestQueue.TryDequeue(out request, out _))
                    continue;
            }

            // Skip cancelled requests immediately
            if (request.CancellationToken.IsCancellationRequested)
            {
                request.Completion.TrySetResult(null);
                continue;
            }

            // Check cache again - another request may have loaded it while queued
            if (_thumbnailService.TryGetCached(request.ImagePath, out var cached))
            {
                request.Completion.TrySetResult(cached);
                continue;
            }

            // Dispatch to background — semaphore is acquired inside ProcessRequestAsync
            _ = ProcessRequestAsync(request);
        }
    }

    /// <summary>
    /// Loads the thumbnail via the underlying service and completes the TCS.
    /// Concurrency is managed by the service's own semaphore.
    /// </summary>
    private async Task ProcessRequestAsync(ThumbnailRequest request)
    {
        try
        {
            var bitmap = await _thumbnailService.LoadThumbnailAsync(
                request.ImagePath, request.TargetWidth, request.CancellationToken).ConfigureAwait(false);
            request.Completion.TrySetResult(bitmap);
        }
        catch (OperationCanceledException)
        {
            request.Completion.TrySetResult(null);
        }
        catch
        {
            request.Completion.TrySetResult(null);
        }
    }

    /// <summary>
    /// Enqueues a request with priority ordering (higher priority = lower queue priority number for min-heap).
    /// </summary>
    private void Enqueue(ThumbnailRequest request)
    {
        // PriorityQueue is a min-heap, so negate the priority value to get max-priority-first ordering
        var queuePriority = -(int)request.Priority;

        lock (_queueLock)
        {
            _requestQueue.Enqueue(request, queuePriority);
        }

        // Signal the processing loop that work is available
        _queueSignal.Release();
    }

    /// <summary>
    /// Gets or creates a CancellationTokenSource for the specified owner.
    /// </summary>
    private CancellationTokenSource GetOrCreateOwnerCts(ThumbnailOwnerToken owner)
    {
        return _ownerCancellations.GetOrAdd(owner, _ => new CancellationTokenSource());
    }

    /// <summary>
    /// Cancels and replaces the CancellationTokenSource for the specified owner.
    /// </summary>
    private void CancelOwnerCts(ThumbnailOwnerToken owner)
    {
        if (_ownerCancellations.TryRemove(owner, out var oldCts))
        {
            try
            {
                oldCts.Cancel();
                oldCts.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed, ignore
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _disposeCts.Cancel();

        // Cancel all owners
        foreach (var kvp in _ownerCancellations)
        {
            try
            {
                kvp.Value.Cancel();
                kvp.Value.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Ignore
            }
        }
        _ownerCancellations.Clear();

        // Drain the queue
        lock (_queueLock)
        {
            while (_requestQueue.TryDequeue(out var request, out _))
            {
                request.Completion.TrySetResult(null);
            }
        }

        try
        {
            _processingLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Ignore timeout / cancellation during shutdown
        }

        _disposeCts.Dispose();
        _queueSignal.Dispose();
    }
}
