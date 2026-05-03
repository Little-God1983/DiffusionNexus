namespace DiffusionNexus.Domain.Services.UnifiedLogging;

using System.ComponentModel;
using System.Runtime.CompilerServices;

/// <summary>
/// Metadata and live state for a tracked long-running task.
/// Mutable progress/status fields are updated by the owning <see cref="ITrackedTaskHandle"/>.
/// </summary>
public sealed class TrackedTaskInfo : INotifyPropertyChanged
{
    private TrackedTaskStatus _status = TrackedTaskStatus.Queued;
    private double _progress = -1;
    private string _statusText = string.Empty;
    private DateTime? _completedAt;

    /// <summary>Unique identifier for this task.</summary>
    public required string TaskId { get; init; }

    /// <summary>Human-readable name (e.g., "Downloading Forge 1.20.1").</summary>
    public required string Name { get; init; }

    /// <summary>Functional category for log filtering.</summary>
    public required LogCategory Category { get; init; }

    /// <summary>Current lifecycle status.</summary>
    public TrackedTaskStatus Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    /// <summary>Progress fraction 0.0–1.0, or -1 for indeterminate.</summary>
    public double Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    /// <summary>Short status text (e.g., "Extracting files… (3/17)").</summary>
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    /// <summary>When the task was created.</summary>
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;

    /// <summary>When the task reached a terminal state, if applicable.</summary>
    public DateTime? CompletedAt
    {
        get => _completedAt;
        set => SetProperty(ref _completedAt, value);
    }

    /// <summary>Cancellation source for cooperative cancellation.</summary>
    public CancellationTokenSource? Cts { get; init; }

    /// <summary>
    /// Returns true when the task is in a terminal state (completed, failed, or cancelled).
    /// </summary>
    public bool IsTerminal => Status is TrackedTaskStatus.Completed
                                     or TrackedTaskStatus.Failed
                                     or TrackedTaskStatus.Cancelled;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        if (propertyName == nameof(Status))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTerminal)));
    }
}
