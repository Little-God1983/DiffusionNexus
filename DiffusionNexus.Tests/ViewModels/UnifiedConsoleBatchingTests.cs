using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// Verifies that <see cref="UnifiedConsoleViewModel"/> coalesces a burst of log
/// entries arriving through <see cref="IUnifiedLogger.LogStream"/> into a single
/// scheduled flush instead of one dispatcher post per entry.
/// </summary>
public class UnifiedConsoleBatchingTests
{
    [Fact]
    public void BurstOfEntries_SchedulesSingleFlush_AndDeliversAll()
    {
        var logger = new FakeUnifiedLogger();
        var vm = new UnifiedConsoleViewModel(logger, new FakeTaskTracker());

        var scheduled = new Queue<Action>();
        vm.ScheduleFlush = scheduled.Enqueue; // replace the dispatcher seam

        for (var i = 0; i < 50; i++)
            logger.Emit(TestLogEntry(i)); // pushes through LogStream

        Assert.Single(scheduled); // burst coalesced into one flush
        scheduled.Dequeue().Invoke();
        Assert.Equal(50, vm.FilteredEntries.Count);

        logger.Emit(TestLogEntry(50)); // after a flush, a new one is scheduled
        Assert.Single(scheduled);
    }

    /// <summary>
    /// Builds a minimal <see cref="LogEntry"/> that passes the ViewModel's default
    /// filters (Info level, no category/task/search/instance filter active).
    /// </summary>
    private static LogEntry TestLogEntry(int i) =>
        new(
            Timestamp: DateTime.UtcNow,
            Level: LogLevel.Info,
            Category: LogCategory.General,
            Source: "Test",
            Message: $"Entry {i}");

    /// <summary>
    /// Minimal <see cref="IUnifiedLogger"/> fake for VM tests: a controllable
    /// <see cref="LogStream"/> ("Emit") plus no-op logging methods. Mirrors the
    /// production <c>UnifiedLogger</c>'s own hand-rolled <see cref="IObservable{T}"/>
    /// so the test doesn't need a System.Reactive dependency.
    /// </summary>
    private sealed class FakeUnifiedLogger : IUnifiedLogger
    {
        private readonly List<IObserver<LogEntry>> _observers = [];

        public FakeUnifiedLogger()
        {
            LogStream = new ObservableStream(this);
        }

        /// <summary>Pushes an entry to every current subscriber, synchronously.</summary>
        public void Emit(LogEntry entry)
        {
            foreach (var observer in _observers.ToArray())
                observer.OnNext(entry);
        }

        public IObservable<LogEntry> LogStream { get; }

        public void Log(LogLevel level, LogCategory category, string source, string message,
            string? detail = null, Exception? ex = null, string? taskId = null)
        { }

        public void Trace(LogCategory category, string source, string message, string? detail = null) { }
        public void Debug(LogCategory category, string source, string message, string? detail = null) { }
        public void Info(LogCategory category, string source, string message, string? detail = null) { }
        public void Warn(LogCategory category, string source, string message, string? detail = null) { }
        public void Error(LogCategory category, string source, string message, Exception? ex = null) { }
        public void Fatal(LogCategory category, string source, string message, Exception ex) { }

        public IReadOnlyList<LogEntry> GetEntries(LogCategory? category = null, LogLevel? minLevel = null) => [];

        public void Clear() { }

        private sealed class ObservableStream(FakeUnifiedLogger owner) : IObservable<LogEntry>
        {
            public IDisposable Subscribe(IObserver<LogEntry> observer)
            {
                owner._observers.Add(observer);
                return new Unsubscriber(owner, observer);
            }
        }

        private sealed class Unsubscriber(FakeUnifiedLogger owner, IObserver<LogEntry> observer) : IDisposable
        {
            public void Dispose() => owner._observers.Remove(observer);
        }
    }

    /// <summary>
    /// Minimal <see cref="ITaskTracker"/> fake. <see cref="UnifiedConsoleViewModel"/>
    /// only subscribes to <see cref="TaskChanged"/> in its constructor; nothing else
    /// is exercised by the batching test.
    /// </summary>
    private sealed class FakeTaskTracker : ITaskTracker
    {
#pragma warning disable CS0067 // fake never needs to raise this; VM only subscribes/unsubscribes
        public event EventHandler<TrackedTaskInfo>? TaskChanged;
#pragma warning restore CS0067

        public IObservable<IReadOnlyList<TrackedTaskInfo>> ActiveTasks { get; } = new EmptyActiveTasksObservable();

        public IReadOnlyList<TrackedTaskInfo> AllTasks => [];

        public ITrackedTaskHandle BeginTask(string name, LogCategory category, CancellationTokenSource? cts = null)
            => throw new NotSupportedException("Not exercised by UnifiedConsoleBatchingTests.");

        public void CancelTask(string taskId) { }

        private sealed class EmptyActiveTasksObservable : IObservable<IReadOnlyList<TrackedTaskInfo>>
        {
            public IDisposable Subscribe(IObserver<IReadOnlyList<TrackedTaskInfo>> observer) => NoopDisposable.Instance;
        }

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
