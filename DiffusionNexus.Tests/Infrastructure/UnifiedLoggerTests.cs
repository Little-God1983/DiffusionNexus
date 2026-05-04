using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Infrastructure.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.Infrastructure;

public class UnifiedLoggerTests
{
    private static UnifiedLogger CreateSut(int? maxEntries = null)
    {
        var logger = new UnifiedLogger();
        if (maxEntries.HasValue) logger.MaxEntries = maxEntries.Value;
        return logger;
    }

    [Fact]
    public void Log_AddsEntryWithProvidedFields()
    {
        var sut = CreateSut();
        var ex = new InvalidOperationException("oops");

        sut.Log(LogLevel.Warning, LogCategory.Network, "src", "msg", "detail", ex, "task-1");

        var entry = sut.GetEntries().Single();
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Category.Should().Be(LogCategory.Network);
        entry.Source.Should().Be("src");
        entry.Message.Should().Be("msg");
        entry.Detail.Should().Be("detail");
        entry.Exception.Should().BeSameAs(ex);
        entry.TaskId.Should().Be("task-1");
    }

    [Theory]
    [InlineData("Trace", LogLevel.Trace)]
    [InlineData("Debug", LogLevel.Debug)]
    [InlineData("Info", LogLevel.Info)]
    [InlineData("Warn", LogLevel.Warning)]
    public void LevelHelpers_ProduceCorrectLevel(string method, LogLevel expected)
    {
        var sut = CreateSut();

        switch (method)
        {
            case "Trace": sut.Trace(LogCategory.General, "s", "m"); break;
            case "Debug": sut.Debug(LogCategory.General, "s", "m"); break;
            case "Info": sut.Info(LogCategory.General, "s", "m"); break;
            case "Warn": sut.Warn(LogCategory.General, "s", "m"); break;
        }

        sut.GetEntries().Single().Level.Should().Be(expected);
    }

    [Fact]
    public void Error_RecordsErrorLevelAndException()
    {
        var sut = CreateSut();
        var ex = new Exception("e");

        sut.Error(LogCategory.General, "s", "m", ex);

        var entry = sut.GetEntries().Single();
        entry.Level.Should().Be(LogLevel.Error);
        entry.Exception.Should().BeSameAs(ex);
    }

    [Fact]
    public void Fatal_RecordsFatalLevelAndException()
    {
        var sut = CreateSut();
        var ex = new Exception("e");

        sut.Fatal(LogCategory.General, "s", "m", ex);

        var entry = sut.GetEntries().Single();
        entry.Level.Should().Be(LogLevel.Fatal);
        entry.Exception.Should().BeSameAs(ex);
    }

    [Fact]
    public void Log_PrunesOldestEntries_WhenAboveMaxEntries()
    {
        var sut = CreateSut(maxEntries: 3);

        for (int i = 0; i < 10; i++)
            sut.Info(LogCategory.General, "s", $"m{i}");

        var entries = sut.GetEntries();
        entries.Should().HaveCount(3);
        entries.Select(e => e.Message).Should().Equal("m7", "m8", "m9");
    }

    [Fact]
    public void GetEntries_FiltersByCategoryAndMinLevel()
    {
        var sut = CreateSut();
        sut.Info(LogCategory.General, "s", "g-info");
        sut.Warn(LogCategory.Network, "s", "n-warn");
        sut.Error(LogCategory.Network, "s", "n-err");
        sut.Debug(LogCategory.Network, "s", "n-dbg");

        var filtered = sut.GetEntries(LogCategory.Network, LogLevel.Warning);

        filtered.Select(e => e.Message).Should().BeEquivalentTo(new[] { "n-warn", "n-err" });
    }

    [Fact]
    public void GetEntries_NoFilters_ReturnsAll()
    {
        var sut = CreateSut();
        sut.Info(LogCategory.General, "s", "a");
        sut.Info(LogCategory.Network, "s", "b");

        sut.GetEntries().Should().HaveCount(2);
    }

    [Fact]
    public void Clear_EmptiesAllEntries()
    {
        var sut = CreateSut();
        sut.Info(LogCategory.General, "s", "m");

        sut.Clear();

        sut.GetEntries().Should().BeEmpty();
    }

    [Fact]
    public void LogStream_NotifiesSubscribersInOrder()
    {
        var sut = CreateSut();
        var received = new List<LogEntry>();
        using var sub = sut.LogStream.Subscribe(new EntryObserver(received));

        sut.Info(LogCategory.General, "s", "1");
        sut.Info(LogCategory.General, "s", "2");

        received.Select(e => e.Message).Should().Equal("1", "2");
    }

    [Fact]
    public void LogStream_DisposingSubscription_StopsReceivingEntries()
    {
        var sut = CreateSut();
        var received = new List<LogEntry>();
        var sub = sut.LogStream.Subscribe(new EntryObserver(received));

        sut.Info(LogCategory.General, "s", "1");
        sub.Dispose();
        sut.Info(LogCategory.General, "s", "2");

        received.Should().HaveCount(1);
        received[0].Message.Should().Be("1");
    }

    [Fact]
    public void LogStream_Subscribe_NullObserver_Throws()
    {
        var sut = CreateSut();

        var act = () => sut.LogStream.Subscribe(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void LogStream_ObserverException_DoesNotBreakLogger()
    {
        var sut = CreateSut();
        using var sub = sut.LogStream.Subscribe(new ThrowingObserver());

        var act = () => sut.Info(LogCategory.General, "s", "m");

        act.Should().NotThrow();
        sut.GetEntries().Should().HaveCount(1);
    }

    private sealed class EntryObserver : IObserver<LogEntry>
    {
        private readonly List<LogEntry> _store;
        public EntryObserver(List<LogEntry> store) => _store = store;
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(LogEntry value) => _store.Add(value);
    }

    private sealed class ThrowingObserver : IObserver<LogEntry>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(LogEntry value) => throw new InvalidOperationException("boom");
    }
}
