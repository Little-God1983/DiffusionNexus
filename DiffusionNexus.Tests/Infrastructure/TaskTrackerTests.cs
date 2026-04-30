using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Infrastructure.Services;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Infrastructure;

public class TaskTrackerTests
{
    private readonly Mock<IUnifiedLogger> _loggerMock = new();

    private TaskTracker CreateSut() => new(_loggerMock.Object);

    [Fact]
    public void BeginTask_AddsRunningTaskAndReturnsHandleWithUniqueId()
    {
        var sut = CreateSut();

        using var handle = sut.BeginTask("download", LogCategory.Download);

        handle.TaskId.Should().NotBeNullOrWhiteSpace();
        handle.CancellationToken.CanBeCanceled.Should().BeTrue();
        sut.AllTasks.Should().ContainSingle();
        sut.AllTasks[0].Status.Should().Be(TrackedTaskStatus.Running);
        sut.AllTasks[0].Name.Should().Be("download");
        sut.AllTasks[0].Category.Should().Be(LogCategory.Download);
    }

    [Fact]
    public void BeginTask_NullName_Throws()
    {
        var sut = CreateSut();

        var act = () => sut.BeginTask(null!, LogCategory.General);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void BeginTask_RaisesTaskChangedEvent()
    {
        var sut = CreateSut();
        TrackedTaskInfo? captured = null;
        sut.TaskChanged += (_, info) => captured = info;

        using var handle = sut.BeginTask("t", LogCategory.General);

        captured.Should().NotBeNull();
        captured!.TaskId.Should().Be(handle.TaskId);
        captured.Status.Should().Be(TrackedTaskStatus.Running);
    }

    [Fact]
    public void ActiveTasks_EmitsSnapshotExcludingTerminalTasks()
    {
        var sut = CreateSut();
        var snapshots = new List<IReadOnlyList<TrackedTaskInfo>>();
        using var sub = sut.ActiveTasks.Subscribe(new ListObserver(snapshots));

        var h1 = sut.BeginTask("a", LogCategory.General);
        var h2 = sut.BeginTask("b", LogCategory.General);
        h1.Complete();

        snapshots.Should().NotBeEmpty();
        var last = snapshots[^1];
        last.Should().ContainSingle(t => t.TaskId == h2.TaskId);
        last.Should().NotContain(t => t.TaskId == h1.TaskId);
    }

    [Fact]
    public void CancelTask_SetsStatusCancelledAndCancelsToken()
    {
        var sut = CreateSut();
        var handle = sut.BeginTask("x", LogCategory.General);

        sut.CancelTask(handle.TaskId);

        var info = sut.AllTasks.Single();
        info.Status.Should().Be(TrackedTaskStatus.Cancelled);
        info.IsTerminal.Should().BeTrue();
        info.CompletedAt.Should().NotBeNull();
        handle.CancellationToken.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void CancelTask_UnknownOrEmptyId_IsNoOp()
    {
        var sut = CreateSut();

        var act1 = () => sut.CancelTask("");
        var act2 = () => sut.CancelTask("does-not-exist");

        act1.Should().NotThrow();
        act2.Should().NotThrow();
        sut.AllTasks.Should().BeEmpty();
    }

    [Fact]
    public void CancelTask_OnTerminalTask_DoesNotChangeStatus()
    {
        var sut = CreateSut();
        var handle = sut.BeginTask("x", LogCategory.General);
        handle.Complete();

        sut.CancelTask(handle.TaskId);

        sut.AllTasks.Single().Status.Should().Be(TrackedTaskStatus.Completed);
    }

    [Fact]
    public void Handle_ReportProgress_ClampsBetweenZeroAndOne()
    {
        var sut = CreateSut();
        using var handle = sut.BeginTask("x", LogCategory.General);

        handle.ReportProgress(2.5, "above");
        sut.AllTasks.Single().Progress.Should().Be(1.0);
        sut.AllTasks.Single().StatusText.Should().Be("above");

        handle.ReportProgress(-1.0, "below");
        sut.AllTasks.Single().Progress.Should().Be(0.0);
    }

    [Fact]
    public void Handle_ReportIndeterminate_SetsProgressNegativeOne()
    {
        var sut = CreateSut();
        using var handle = sut.BeginTask("x", LogCategory.General);

        handle.ReportIndeterminate("working");

        sut.AllTasks.Single().Progress.Should().Be(-1);
        sut.AllTasks.Single().StatusText.Should().Be("working");
    }

    [Fact]
    public void Handle_Complete_SetsTerminalStateAndProgressOne()
    {
        var sut = CreateSut();
        var handle = sut.BeginTask("x", LogCategory.General);

        handle.Complete("done");

        var info = sut.AllTasks.Single();
        info.Status.Should().Be(TrackedTaskStatus.Completed);
        info.Progress.Should().Be(1.0);
        info.StatusText.Should().Be("done");
        info.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public void Handle_Fail_SetsFailedStatusAndLogsError()
    {
        var sut = CreateSut();
        var handle = sut.BeginTask("x", LogCategory.General);
        var ex = new InvalidOperationException("boom");

        handle.Fail(ex);

        var info = sut.AllTasks.Single();
        info.Status.Should().Be(TrackedTaskStatus.Failed);
        info.StatusText.Should().Be("boom");
        _loggerMock.Verify(l => l.Error(LogCategory.General, "x", It.IsAny<string>(), ex), Times.Once);
    }

    [Fact]
    public void Handle_Dispose_AutoCompletesIfNotTerminal()
    {
        var sut = CreateSut();
        var handle = sut.BeginTask("x", LogCategory.General);

        handle.Dispose();

        sut.AllTasks.Single().Status.Should().Be(TrackedTaskStatus.Completed);
    }

    [Fact]
    public void Handle_Dispose_DoesNotOverrideTerminalStatus()
    {
        var sut = CreateSut();
        var handle = sut.BeginTask("x", LogCategory.General);
        handle.Fail(new Exception("bad"));

        handle.Dispose();

        sut.AllTasks.Single().Status.Should().Be(TrackedTaskStatus.Failed);
    }

    [Fact]
    public void Handle_LogAndReportProgress_AfterTerminal_AreIgnored()
    {
        var sut = CreateSut();
        var handle = sut.BeginTask("x", LogCategory.General);
        handle.Complete();

        var beforeProgress = sut.AllTasks.Single().Progress;
        handle.ReportProgress(0.5, "ignored");
        handle.ReportIndeterminate("ignored");

        sut.AllTasks.Single().Progress.Should().Be(beforeProgress);
        sut.AllTasks.Single().StatusText.Should().NotBe("ignored");
    }

    [Fact]
    public void BeginTask_CreatesLinkedCancellationToken_WhenNoneProvided()
    {
        var sut = CreateSut();
        using var cts = new CancellationTokenSource();

        using var handle = sut.BeginTask("x", LogCategory.General, cts);
        cts.Cancel();

        handle.CancellationToken.IsCancellationRequested.Should().BeTrue();
    }

    private sealed class ListObserver : IObserver<IReadOnlyList<TrackedTaskInfo>>
    {
        private readonly List<IReadOnlyList<TrackedTaskInfo>> _store;
        public ListObserver(List<IReadOnlyList<TrackedTaskInfo>> store) => _store = store;
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(IReadOnlyList<TrackedTaskInfo> value) => _store.Add(value);
    }
}
