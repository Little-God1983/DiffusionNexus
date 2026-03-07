using System.Collections.Concurrent;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using SerilogLogger = Serilog.Log;

namespace DiffusionNexus.Infrastructure.Services;

/// <summary>
/// Thread-safe implementation of <see cref="IUnifiedLogger"/>.
/// All log entries flow through Serilog for file persistence and through
/// an observable stream for real-time UI binding.
/// </summary>
public sealed class UnifiedLogger : IUnifiedLogger
{
    private const int DefaultMaxEntries = 2000;

    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly LogEntrySubject _logSubject = new();
    private int _entryCount;

    /// <summary>
    /// Maximum number of log entries retained in memory. Older entries are pruned.
    /// </summary>
    public int MaxEntries { get; set; } = DefaultMaxEntries;

    #region IUnifiedLogger – Basic Logging

    /// <inheritdoc />
    public void Log(LogLevel level, LogCategory category, string source, string message,
                    string? detail = null, Exception? ex = null, string? taskId = null)
    {
        var entry = new LogEntry(
            Timestamp: DateTime.UtcNow,
            Level: level,
            Category: category,
            Source: source,
            Message: message,
            Detail: detail ?? ex?.StackTrace,
            TaskId: taskId,
            Exception: ex);

        _entries.Enqueue(entry);
        Interlocked.Increment(ref _entryCount);
        PruneIfNeeded();

        LogToSerilog(entry);
        _logSubject.OnNext(entry);
    }

    /// <inheritdoc />
    public void Trace(LogCategory category, string source, string message, string? detail = null)
        => Log(LogLevel.Trace, category, source, message, detail);

    /// <inheritdoc />
    public void Debug(LogCategory category, string source, string message, string? detail = null)
        => Log(LogLevel.Debug, category, source, message, detail);

    /// <inheritdoc />
    public void Info(LogCategory category, string source, string message, string? detail = null)
        => Log(LogLevel.Info, category, source, message, detail);

    /// <inheritdoc />
    public void Warn(LogCategory category, string source, string message, string? detail = null)
        => Log(LogLevel.Warning, category, source, message, detail);

    /// <inheritdoc />
    public void Error(LogCategory category, string source, string message, Exception? ex = null)
        => Log(LogLevel.Error, category, source, message, ex: ex);

    /// <inheritdoc />
    public void Fatal(LogCategory category, string source, string message, Exception ex)
        => Log(LogLevel.Fatal, category, source, message, ex: ex);

    #endregion

    #region IUnifiedLogger – Observable Stream

    /// <inheritdoc />
    public IObservable<LogEntry> LogStream => _logSubject;

    #endregion

    #region IUnifiedLogger – Query

    /// <inheritdoc />
    public IReadOnlyList<LogEntry> GetEntries(LogCategory? category = null, LogLevel? minLevel = null)
    {
        IEnumerable<LogEntry> query = _entries;

        if (category.HasValue)
            query = query.Where(e => e.Category == category.Value);

        if (minLevel.HasValue)
            query = query.Where(e => e.Level >= minLevel.Value);

        return query.ToArray();
    }

    #endregion

    #region IUnifiedLogger – Lifecycle

    /// <inheritdoc />
    public void Clear()
    {
        while (_entries.TryDequeue(out _))
            Interlocked.Decrement(ref _entryCount);
    }

    #endregion

    #region Private Helpers

    private void PruneIfNeeded()
    {
        while (_entryCount > MaxEntries && _entries.TryDequeue(out _))
            Interlocked.Decrement(ref _entryCount);
    }

    private static void LogToSerilog(LogEntry entry)
    {
        var template = "[{Category}] [{Source}] {Message}";

        switch (entry.Level)
        {
            case LogLevel.Trace:
                SerilogLogger.Verbose(template, entry.Category, entry.Source, entry.Message);
                break;
            case LogLevel.Debug:
                SerilogLogger.Debug(template, entry.Category, entry.Source, entry.Message);
                break;
            case LogLevel.Info:
                SerilogLogger.Information(template, entry.Category, entry.Source, entry.Message);
                break;
            case LogLevel.Warning:
                SerilogLogger.Warning(template, entry.Category, entry.Source, entry.Message);
                break;
            case LogLevel.Error:
                if (entry.Exception is not null)
                    SerilogLogger.Error(entry.Exception, template, entry.Category, entry.Source, entry.Message);
                else
                    SerilogLogger.Error(template + " Detail: {Detail}", entry.Category, entry.Source, entry.Message, entry.Detail);
                break;
            case LogLevel.Fatal:
                SerilogLogger.Fatal(entry.Exception, template, entry.Category, entry.Source, entry.Message);
                break;
        }
    }

    #endregion

    /// <summary>
    /// Minimal IObservable implementation backed by a thread-safe subscriber list.
    /// Avoids taking a dependency on System.Reactive.
    /// </summary>
    private sealed class LogEntrySubject : IObservable<LogEntry>
    {
        private readonly List<IObserver<LogEntry>> _observers = [];
        private readonly object _lock = new();

        public IDisposable Subscribe(IObserver<LogEntry> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            lock (_lock) _observers.Add(observer);
            return new Unsubscriber(this, observer);
        }

        public void OnNext(LogEntry entry)
        {
            IObserver<LogEntry>[] snapshot;
            lock (_lock) snapshot = [.. _observers];

            foreach (var observer in snapshot)
            {
                try { observer.OnNext(entry); }
                catch { /* observer failure must not break the logger */ }
            }
        }

        private void Remove(IObserver<LogEntry> observer)
        {
            lock (_lock) _observers.Remove(observer);
        }

        private sealed class Unsubscriber(LogEntrySubject subject, IObserver<LogEntry> observer) : IDisposable
        {
            public void Dispose() => subject.Remove(observer);
        }
    }
}
