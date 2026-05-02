using DiffusionNexus.Domain.Services;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Domain.Services;

/// <summary>
/// Unit tests for <see cref="ActivityLogExtensions"/> and the internal
/// <c>SourcedActivityLogger</c> facade.
/// </summary>
public class ActivityLogExtensionsTests
{
    private readonly Mock<IActivityLogService> _logService = new(MockBehavior.Strict);

    [Fact]
    public void Info_LogsEntryWithInfoSeverity_AndReturnsIt()
    {
        ActivityLogEntry? captured = null;
        _logService.Setup(s => s.Log(It.IsAny<ActivityLogEntry>()))
                   .Callback<ActivityLogEntry>(e => captured = e);

        var result = _logService.Object.Info("MySource", "hello", "details");

        result.Should().BeSameAs(captured);
        result.Severity.Should().Be(ActivitySeverity.Info);
        result.Source.Should().Be("MySource");
        result.Message.Should().Be("hello");
        result.Details.Should().Be("details");
        _logService.Verify(s => s.Log(It.IsAny<ActivityLogEntry>()), Times.Once);
    }

    [Fact]
    public void Success_LogsEntryWithSuccessSeverity()
    {
        ActivityLogEntry? captured = null;
        _logService.Setup(s => s.Log(It.IsAny<ActivityLogEntry>()))
                   .Callback<ActivityLogEntry>(e => captured = e);

        var result = _logService.Object.Success("Src", "ok");

        captured.Should().BeSameAs(result);
        result.Severity.Should().Be(ActivitySeverity.Success);
    }

    [Fact]
    public void Warning_LogsEntryWithWarningSeverity()
    {
        _logService.Setup(s => s.Log(It.IsAny<ActivityLogEntry>()));

        var result = _logService.Object.Warning("Src", "watch out");

        result.Severity.Should().Be(ActivitySeverity.Warning);
        _logService.Verify(s => s.Log(It.Is<ActivityLogEntry>(
            e => e.Severity == ActivitySeverity.Warning && e.Message == "watch out")), Times.Once);
    }

    [Fact]
    public void Error_StringOverload_LogsEntryWithErrorSeverity()
    {
        _logService.Setup(s => s.Log(It.IsAny<ActivityLogEntry>()));

        var result = _logService.Object.Error("Src", "boom", "stacktrace");

        result.Severity.Should().Be(ActivitySeverity.Error);
        result.Details.Should().Be("stacktrace");
    }

    [Fact]
    public void Error_ExceptionOverload_LogsExceptionMessageAsDetails()
    {
        ActivityLogEntry? captured = null;
        _logService.Setup(s => s.Log(It.IsAny<ActivityLogEntry>()))
                   .Callback<ActivityLogEntry>(e => captured = e);
        var ex = new InvalidOperationException("bad state");

        var result = _logService.Object.Error("Src", "operation failed", ex);

        captured.Should().BeSameAs(result);
        result.Severity.Should().Be(ActivitySeverity.Error);
        result.Details.Should().Be("bad state");
    }

    [Fact]
    public void BeginOperation_DelegatesToStartOperation()
    {
        using var op = new ProgressOperation("name", "src");
        _logService.Setup(s => s.StartOperation("name", "src", true)).Returns(op);

        var returned = _logService.Object.BeginOperation("name", "src", isCancellable: true);

        returned.Should().BeSameAs(op);
        _logService.Verify(s => s.StartOperation("name", "src", true), Times.Once);
    }

    [Fact]
    public void ForSource_ReturnsLoggerThatPassesFixedSourceToAllCalls()
    {
        const string source = "MyComponent";
        _logService.Setup(s => s.LogInfo(source, "i", null));
        _logService.Setup(s => s.LogSuccess(source, "s", null));
        _logService.Setup(s => s.LogWarning(source, "w", null));
        _logService.Setup(s => s.LogError(source, "e", (string?)null));
        _logService.Setup(s => s.LogDebug(source, "d", null));
        _logService.Setup(s => s.SetStatus("status", ActivitySeverity.Info));

        var logger = _logService.Object.ForSource(source);

        logger.Info("i");
        logger.Success("s");
        logger.Warning("w");
        logger.Error("e");
        logger.Debug("d");
        logger.SetStatus("status");

        _logService.Verify(s => s.LogInfo(source, "i", null), Times.Once);
        _logService.Verify(s => s.LogSuccess(source, "s", null), Times.Once);
        _logService.Verify(s => s.LogWarning(source, "w", null), Times.Once);
        _logService.Verify(s => s.LogError(source, "e", (string?)null), Times.Once);
        _logService.Verify(s => s.LogDebug(source, "d", null), Times.Once);
        _logService.Verify(s => s.SetStatus("status", ActivitySeverity.Info), Times.Once);
    }

    [Fact]
    public void ForSource_ErrorWithException_ForwardsExceptionToLogService()
    {
        const string source = "MyComponent";
        var ex = new InvalidOperationException("oops");
        _logService.Setup(s => s.LogError(source, "msg", ex));

        var logger = _logService.Object.ForSource(source);
        logger.Error("msg", ex);

        _logService.Verify(s => s.LogError(source, "msg", ex), Times.Once);
    }

    [Fact]
    public void ForSource_BeginOperation_StartsOperationWithFixedSource()
    {
        const string source = "MyComponent";
        using var op = new ProgressOperation("work", source);
        _logService.Setup(s => s.StartOperation("work", source, false)).Returns(op);

        var logger = _logService.Object.ForSource(source);
        var returned = logger.BeginOperation("work");

        returned.Should().BeSameAs(op);
    }
}
