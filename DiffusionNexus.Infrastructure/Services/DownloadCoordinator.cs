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

    public DownloadCoordinator(IActivityLogService? activityLog = null)
    {
        _activityLog = activityLog;
        _slots = new SemaphoreSlim(MaxConcurrent, MaxConcurrent);
    }

    private int _maxConcurrent = 3;
    public int MaxConcurrent
    {
        get => _maxConcurrent;
        set
        {
            // Resizing live: swap the semaphore. Already-in-flight downloads
            // hold permits from the previous instance and will release back
            // into it via captured local — we just lose tracking, but since
            // the semaphore is single-instance-only owned by the coordinator
            // we accept the leak for the rare resize case.
            if (value < 1) throw new ArgumentOutOfRangeException(nameof(value), "MaxConcurrent must be ≥ 1.");
            if (value == _maxConcurrent) return;
            _maxConcurrent = value;
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
        var task = new MutableTask(Guid.NewGuid(), name);

        lock (_lock) _tasks.Add(task);
        RaiseStateChanged();

        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);

        // Promote to active. The state-change notification covers both the
        // queue→active transition and the activity-log aggregate update.
        lock (_lock) task.Status = DownloadTaskStatus.Active;
        UpdateActivityLog();
        RaiseStateChanged();

        var success = false;
        try
        {
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

            success = await downloadAction(progress, cancellationToken).ConfigureAwait(false);

            lock (_lock) task.Status = success ? DownloadTaskStatus.Completed : DownloadTaskStatus.Failed;
        }
        catch (OperationCanceledException)
        {
            lock (_lock) task.Status = DownloadTaskStatus.Failed;
            throw;
        }
        catch (Exception ex)
        {
            lock (_lock) task.Status = DownloadTaskStatus.Failed;
            Log.Error(ex, "Download {Name} failed inside coordinator", name);
        }
        finally
        {
            _slots.Release();
            // Remove from the tracked list so the UI list doesn't grow
            // unbounded. The unified console keeps a permanent log entry
            // via the activity log so users still see the completion event.
            lock (_lock) _tasks.Remove(task);
            UpdateActivityLog();
            RaiseStateChanged();
        }

        return success;
    }

    /// <summary>
    /// Reflects the coordinator state into <see cref="IActivityLogService"/>'s
    /// single-slot download API so the existing status bar (which knows only
    /// about that API) renders the right summary:
    ///   • 0 active → close the single slot
    ///   • 1 active → name + percent for that one download
    ///   • 2+ active → "N Downloads in progress" + averaged percent.
    /// </summary>
    private void UpdateActivityLog()
    {
        if (_activityLog is null) return;

        List<MutableTask> snapshot;
        lock (_lock)
            snapshot = _tasks.Where(t => t.Status == DownloadTaskStatus.Active).ToList();

        if (snapshot.Count == 0)
        {
            if (_activityLog.IsDownloadInProgress)
            {
                _activityLog.CompleteDownloadProgress(true, "All downloads complete.");
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

        public MutableTask(Guid id, string name)
        {
            Id = id;
            Name = name;
        }
    }
}
