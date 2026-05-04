using System.Collections.Concurrent;
using DiffusionNexus.Domain.Services.UnifiedLogging;

namespace DiffusionNexus.Infrastructure.Services;

/// <summary>
/// Thread-safe implementation of <see cref="ITaskTracker"/>.
/// Manages the lifecycle of tracked long-running tasks and emits observable updates.
/// </summary>
public sealed class TaskTracker : ITaskTracker
{
    private readonly IUnifiedLogger _logger;
    private readonly ConcurrentDictionary<string, TrackedTaskInfo> _tasks = new();
    private readonly ActiveTasksSubject _activeTasksSubject = new();

    /// <inheritdoc />
    public event EventHandler<TrackedTaskInfo>? TaskChanged;

    public TaskTracker(IUnifiedLogger logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public ITrackedTaskHandle BeginTask(string name, LogCategory category, CancellationTokenSource? cts = null)
    {
        ArgumentNullException.ThrowIfNull(name);

        cts ??= new CancellationTokenSource();
        var taskId = Guid.NewGuid().ToString("N")[..12];

        var info = new TrackedTaskInfo
        {
            TaskId = taskId,
            Name = name,
            Category = category,
            Status = TrackedTaskStatus.Running,
            Cts = cts,
            StartedAt = DateTime.UtcNow
        };

        _tasks[taskId] = info;
        NotifyChanged(info);

        _logger.Log(LogLevel.Debug, category, nameof(TaskTracker), $"Task started: {name}", taskId: taskId);

        return new TrackedTaskHandle(info, _logger, OnTaskTerminated);
    }

    /// <inheritdoc />
    public IObservable<IReadOnlyList<TrackedTaskInfo>> ActiveTasks => _activeTasksSubject;

    /// <inheritdoc />
    public IReadOnlyList<TrackedTaskInfo> AllTasks => [.. _tasks.Values];

    /// <inheritdoc />
    public void CancelTask(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return;

        if (_tasks.TryGetValue(taskId, out var info) && !info.IsTerminal)
        {
            info.Cts?.Cancel();
            info.Status = TrackedTaskStatus.Cancelled;
            info.CompletedAt = DateTime.UtcNow;
            _logger.Log(LogLevel.Warning, info.Category, nameof(TaskTracker), $"Task cancelled: {info.Name}", taskId: taskId);
            NotifyChanged(info);
        }
    }

    private void OnTaskTerminated(TrackedTaskInfo info)
    {
        NotifyChanged(info);
    }

    private void NotifyChanged(TrackedTaskInfo info)
    {
        TaskChanged?.Invoke(this, info);

        var activeTasks = _tasks.Values
            .Where(t => !t.IsTerminal)
            .ToArray();
        _activeTasksSubject.OnNext(activeTasks);
    }

    /// <summary>
    /// Minimal IObservable that emits snapshots of active tasks.
    /// </summary>
    private sealed class ActiveTasksSubject : IObservable<IReadOnlyList<TrackedTaskInfo>>
    {
        private readonly List<IObserver<IReadOnlyList<TrackedTaskInfo>>> _observers = [];
        private readonly object _lock = new();

        public IDisposable Subscribe(IObserver<IReadOnlyList<TrackedTaskInfo>> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            lock (_lock) _observers.Add(observer);
            return new Unsubscriber(this, observer);
        }

        public void OnNext(IReadOnlyList<TrackedTaskInfo> value)
        {
            IObserver<IReadOnlyList<TrackedTaskInfo>>[] snapshot;
            lock (_lock) snapshot = [.. _observers];
            foreach (var o in snapshot)
            {
                try { o.OnNext(value); }
                catch { /* observer failure must not break the tracker */ }
            }
        }

        private void Remove(IObserver<IReadOnlyList<TrackedTaskInfo>> observer)
        {
            lock (_lock) _observers.Remove(observer);
        }

        private sealed class Unsubscriber(ActiveTasksSubject subject, IObserver<IReadOnlyList<TrackedTaskInfo>> observer) : IDisposable
        {
            public void Dispose() => subject.Remove(observer);
        }
    }
}

/// <summary>
/// Handle that tracks a single long-running task.
/// Logs and progress updates are automatically linked by TaskId.
/// </summary>
internal sealed class TrackedTaskHandle : ITrackedTaskHandle
{
    private readonly TrackedTaskInfo _info;
    private readonly IUnifiedLogger _logger;
    private readonly Action<TrackedTaskInfo> _onTerminated;
    private bool _disposed;

    internal TrackedTaskHandle(TrackedTaskInfo info, IUnifiedLogger logger, Action<TrackedTaskInfo> onTerminated)
    {
        _info = info;
        _logger = logger;
        _onTerminated = onTerminated;
    }

    /// <inheritdoc />
    public string TaskId => _info.TaskId;

    /// <inheritdoc />
    public CancellationToken CancellationToken => _info.Cts?.Token ?? CancellationToken.None;

    /// <inheritdoc />
    public void ReportProgress(double progress, string? statusText = null)
    {
        if (_info.IsTerminal) return;

        _info.Progress = Math.Clamp(progress, 0.0, 1.0);
        if (statusText is not null) _info.StatusText = statusText;
        _onTerminated(_info); // reuse for change notification
    }

    /// <inheritdoc />
    public void ReportIndeterminate(string? statusText = null)
    {
        if (_info.IsTerminal) return;

        _info.Progress = -1;
        if (statusText is not null) _info.StatusText = statusText;
        _onTerminated(_info);
    }

    /// <inheritdoc />
    public void Complete(string? message = null)
    {
        if (_info.IsTerminal) return;

        _info.Status = TrackedTaskStatus.Completed;
        _info.Progress = 1.0;
        _info.CompletedAt = DateTime.UtcNow;
        if (message is not null) _info.StatusText = message;

        var elapsed = _info.CompletedAt.Value - _info.StartedAt;
        _logger.Log(LogLevel.Info, _info.Category, _info.Name,
            message ?? $"Completed in {elapsed.TotalSeconds:F1}s",
            taskId: _info.TaskId);
        _onTerminated(_info);
    }

    /// <inheritdoc />
    public void Fail(Exception ex, string? message = null)
    {
        if (_info.IsTerminal) return;

        _info.Status = TrackedTaskStatus.Failed;
        _info.CompletedAt = DateTime.UtcNow;
        _info.StatusText = message ?? ex.Message;

        _logger.Error(_info.Category, _info.Name,
            message ?? $"Failed: {ex.Message}", ex);
        _onTerminated(_info);
    }

    /// <inheritdoc />
    public void Log(LogLevel level, string message)
    {
        _logger.Log(level, _info.Category, _info.Name, message, taskId: _info.TaskId);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Auto-complete if not already terminated
        if (!_info.IsTerminal)
        {
            Complete();
        }
    }
}
