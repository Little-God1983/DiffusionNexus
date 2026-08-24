using DiffusionNexus.Domain.Services;
using Serilog;

namespace DiffusionNexus.Infrastructure.Services;

/// <summary>
/// Concurrent download coordinator with a fixed-size semaphore (default 3
/// slots). Excess <see cref="EnqueueAsync"/> calls block at the semaphore
/// until a slot frees, so the queue is implicit — the order of admission is
/// the order of <see cref="SemaphoreSlim.WaitAsync"/> wakeups, which is FIFO
/// in .NET for fairness purposes. The coordinator owns the single-slot
/// <see cref="IActivityLogService"/> shim so the existing status-bar UI sees
/// either the only active download's name or an aggregated "N downloads in
/// progress" line.
/// </summary>
public sealed class DownloadCoordinator : IDownloadCoordinator, IDisposable
{
    private readonly IActivityLogService? _activityLog;
    private readonly object _lock = new();
    private readonly List<MutableTask> _tasks = new();
    private SemaphoreSlim _slots;
    private string? _lastActivityLogName;
    private bool _disposed;

    // Terminal outcomes since the last batch summary. Tasks are removed from
    // _tasks the moment they finish, so without these counters the drain
    // report has no memory of what happened — it used to hardcode success and
    // showed a green "All downloads complete." over failed batches.
    private int _batchSucceeded;
    private int _batchFailed;
    private int _batchCancelled;

    public DownloadCoordinator(IActivityLogService? activityLog = null)
    {
        _activityLog = activityLog;
        _slots = new SemaphoreSlim(MaxConcurrent, MaxConcurrent);
    }

    private int _maxConcurrent = 3;

    /// <summary>
    /// Maximum number of downloads the coordinator admits concurrently. Setting this swaps in a
    /// fresh <see cref="SemaphoreSlim"/> sized to the new value.
    /// <para>
    /// <see cref="EnqueueAsync"/> captures the current semaphore into a local once, before
    /// <c>WaitAsync</c>, and releases through that same local — so an in-flight download always
    /// finishes its wait/release pair on the instance it started with, even if this setter swaps
    /// <c>_slots</c> mid-flight.
    /// </para>
    /// <para>
    /// Trade-off (accepted, not a bug): during a swap window the total number of simultaneously
    /// active downloads can transiently exceed the newly configured limit — old in-flight
    /// downloads keep running under the old semaphore's permits while new admissions are governed
    /// by the new one. The invariant this class guarantees is "no semaphore instance is ever
    /// over-released", not "the global active count is capped at every instant during a resize".
    /// </para>
    /// </summary>
    public int MaxConcurrent
    {
        get => _maxConcurrent;
        set
        {
            if (value < 1) throw new ArgumentOutOfRangeException(nameof(value), "MaxConcurrent must be ≥ 1.");
            if (value == _maxConcurrent) return;
            _maxConcurrent = value;

            // Deliberately NOT disposing the swapped-out semaphore: in-flight downloads captured
            // it into a local (see EnqueueAsync) and will Release() it later; disposing it here
            // would make that Release() throw ObjectDisposedException. This class never
            // materializes AvailableWaitHandle, so SemaphoreSlim.Dispose() has nothing
            // load-bearing to clean up — letting the old instance be GC'd once the last
            // in-flight holder releases it is simpler and safe.
            _slots = new SemaphoreSlim(value, value);
            RaiseStateChanged();
        }
    }

    public IReadOnlyList<DownloadTask> All
    {
        get { lock (_lock) return _tasks.Select(Snapshot).ToList(); }
    }

    public IReadOnlyList<DownloadTask> Active
    {
        get { lock (_lock) return _tasks.Where(t => t.Status == DownloadTaskStatus.Active).Select(Snapshot).ToList(); }
    }

    public IReadOnlyList<DownloadTask> Queued
    {
        get { lock (_lock) return _tasks.Where(t => t.Status == DownloadTaskStatus.Queued).Select(Snapshot).ToList(); }
    }

    public int AverageActivePercent
    {
        get
        {
            lock (_lock)
            {
                var actives = _tasks.Where(t => t.Status == DownloadTaskStatus.Active).ToList();
                if (actives.Count == 0) return 0;
                return (int)Math.Round(actives.Average(t => t.Percent));
            }
        }
    }

    public event EventHandler? StateChanged;

    public async Task<bool> EnqueueAsync(
        string name,
        Func<IProgress<DownloadTaskProgress>, CancellationToken, Task<bool>> downloadAction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(downloadAction);

        // Linked CTS: external cancellation (e.g. app shutdown) + per-task
        // cancellation (Cancel button in the flyout) both signal the action.
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var task = new MutableTask(Guid.NewGuid(), name) { Cts = linkedCts };

        lock (_lock) _tasks.Add(task);
        RaiseStateChanged();

        var success = false;
        var slotAcquired = false;

        // Capture the semaphore instance once, before waiting on it. MaxConcurrent's setter can
        // swap the _slots field concurrently; capturing here guarantees this download's wait and
        // its eventual release always target the SAME instance, even if a resize happens while
        // this download is in flight (issue #467).
        var slots = _slots;

        try
        {
            // Cancelling a queued task wakes us right back up; the resulting
            // OperationCanceledException is caught below and the status flips
            // to Cancelled before we ever consume a semaphore slot.
            await slots.WaitAsync(linkedCts.Token).ConfigureAwait(false);
            slotAcquired = true;

            // Promote to active. The state-change notification covers both the
            // queue→active transition and the activity-log aggregate update.
            lock (_lock) task.Status = DownloadTaskStatus.Active;
            UpdateActivityLog();
            RaiseStateChanged();

            var progress = new Progress<DownloadTaskProgress>(p =>
            {
                lock (_lock)
                {
                    task.Percent = Math.Clamp(p.Percent, 0, 100);
                    task.StatusMessage = p.StatusMessage;
                }
                UpdateActivityLog();
                RaiseStateChanged();
            });

            success = await downloadAction(progress, linkedCts.Token).ConfigureAwait(false);

            lock (_lock)
            {
                task.Status = success ? DownloadTaskStatus.Completed : DownloadTaskStatus.Failed;
                if (success) _batchSucceeded++; else _batchFailed++;
            }
        }
        catch (OperationCanceledException)
        {
            // Distinguish user cancellation from a generic failure so the UI
            // shows the right colour and the activity log message reads
            // "cancelled" rather than "failed".
            lock (_lock)
            {
                task.Status = DownloadTaskStatus.Cancelled;
                _batchCancelled++;
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                task.Status = DownloadTaskStatus.Failed;
                _batchFailed++;
            }
            Log.Error(ex, "Download {Name} failed inside coordinator", name);
        }
        finally
        {
            if (slotAcquired) slots.Release();
            // Remove from the tracked list so the UI list doesn't grow
            // unbounded. The unified console keeps a permanent log entry
            // via the activity log so users still see the completion event.
            lock (_lock) _tasks.Remove(task);
            UpdateActivityLog();
            RaiseStateChanged();
            linkedCts.Dispose();
        }

        return success;
    }

    public void Cancel(Guid taskId)
    {
        CancellationTokenSource? cts = null;
        lock (_lock)
        {
            var task = _tasks.FirstOrDefault(t => t.Id == taskId);
            if (task is null) return;
            cts = task.Cts;
        }
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { /* completed in the meantime */ }
    }

    /// <summary>
    /// Reflects the coordinator state into <see cref="IActivityLogService"/>'s
    /// single-slot download API so the existing status bar (which knows only
    /// about that API) renders the right summary:
    ///   • 0 active → close the single slot
    ///   • 1 active → name + percent for that one download
    ///   • 2+ active → "N Downloads in progress" + averaged percent.
    /// </summary>
    /// <summary>
    /// Guarded the same way <see cref="RaiseStateChanged"/> is: <see cref="_activityLog"/> is an
    /// external dependency called from inside <see cref="EnqueueAsync"/>'s try block, so a throw
    /// from it used to be indistinguishable from the download work itself failing — the task got
    /// marked Failed without <c>downloadAction</c> ever running, which <c>CivitaiModelDownloader</c>
    /// then reports as Cancelled (spec §4.4 D3 gotcha). Swallow and log instead of restructuring
    /// the three call sites inside <see cref="EnqueueAsync"/>.
    /// </summary>
    private void UpdateActivityLog()
    {
        try
        {
            UpdateActivityLogCore();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DownloadCoordinator UpdateActivityLog threw");
        }
    }

    private void UpdateActivityLogCore()
    {
        if (_activityLog is null) return;

        List<MutableTask> snapshot;
        lock (_lock)
            snapshot = _tasks.Where(t => t.Status == DownloadTaskStatus.Active).ToList();

        if (snapshot.Count == 0)
        {
            // Consume the batch counters even when no summary gets shown
            // (e.g. a task cancelled while still queued, outside any visible
            // batch) so stale outcomes never bleed into the next batch.
            int succeeded, failed, cancelled;
            lock (_lock)
            {
                succeeded = _batchSucceeded;
                failed = _batchFailed;
                cancelled = _batchCancelled;
                _batchSucceeded = _batchFailed = _batchCancelled = 0;
            }

            if (_activityLog.IsDownloadInProgress)
            {
                _activityLog.CompleteDownloadProgress(
                    failed == 0 && cancelled == 0,
                    ComposeBatchSummary(succeeded, failed, cancelled));
            }
            _lastActivityLogName = null;
            return;
        }

        string name;
        int percent;
        string? message;

        if (snapshot.Count == 1)
        {
            var only = snapshot[0];
            name = only.Name;
            percent = only.Percent;
            message = only.StatusMessage;
        }
        else
        {
            name = $"{snapshot.Count} Downloads in progress";
            percent = (int)Math.Round(snapshot.Average(t => t.Percent));
            message = null;
        }

        // StartDownloadProgress resets the slot, so only re-issue it when the
        // displayed name actually changes (e.g. 1→2 transition or different
        // single-active model). Otherwise just push the new percent so the
        // status bar progress bar animates smoothly.
        if (!string.Equals(_lastActivityLogName, name, StringComparison.Ordinal))
        {
            _activityLog.StartDownloadProgress(name);
            _lastActivityLogName = name;
        }
        _activityLog.ReportDownloadProgress(percent, message);
    }

    /// <summary>
    /// Human-readable batch verdict for the status bar. Failures lead the
    /// sentence — the red ribbon's first words must say what went wrong.
    /// </summary>
    private static string ComposeBatchSummary(int succeeded, int failed, int cancelled)
    {
        var total = succeeded + failed + cancelled;
        if (failed == 0 && cancelled == 0)
        {
            return total == 1 ? "Download complete." : $"All {total} downloads complete.";
        }

        var parts = new List<string>(3);
        if (failed > 0) parts.Add($"{failed} failed");
        if (cancelled > 0) parts.Add($"{cancelled} cancelled");
        if (succeeded > 0) parts.Add($"{succeeded} completed");
        return $"Downloads finished — {string.Join(", ", parts)} (of {total}).";
    }

    private static DownloadTask Snapshot(MutableTask t) =>
        new(t.Id, t.Name, t.Status, t.Percent, t.StatusMessage);

    private void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log.Warning(ex, "DownloadCoordinator StateChanged subscriber threw"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Only the CURRENT _slots is disposed here — that's fine by the same reasoning as the
        // MaxConcurrent setter: any already-swapped-out (old) instance was never captured by this
        // method, and in-flight downloads on the current instance are expected to have completed
        // before the coordinator itself is disposed.
        _slots.Dispose();
    }

    /// <summary>
    /// Internal mutable counterpart of the public <see cref="DownloadTask"/>
    /// record. Lives only inside the coordinator lock.
    /// </summary>
    private sealed class MutableTask
    {
        public Guid Id { get; }
        public string Name { get; }
        public DownloadTaskStatus Status { get; set; } = DownloadTaskStatus.Queued;
        public int Percent { get; set; }
        public string? StatusMessage { get; set; }
        public CancellationTokenSource? Cts { get; set; }

        public MutableTask(Guid id, string name)
        {
            Id = id;
            Name = name;
        }
    }
}
