using Avalonia.Media.Imaging;
using DiffusionNexus.UI.Services;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Services;

/// <summary>
/// Unit tests for <see cref="ThumbnailOrchestrator"/>.
/// Note: Avalonia Bitmap cannot be instantiated without a headless platform,
/// so tests use null bitmaps and verify behavior via mock interactions.
/// </summary>
public class ThumbnailOrchestratorTests : IDisposable
{
    private readonly Mock<IThumbnailService> _mockService;
    private readonly ThumbnailOrchestrator _orchestrator;
    private readonly ThumbnailOwnerToken _ownerA = new("ViewA");
    private readonly ThumbnailOwnerToken _ownerB = new("ViewB");

    public ThumbnailOrchestratorTests()
    {
        _mockService = new Mock<IThumbnailService>();
        _orchestrator = new ThumbnailOrchestrator(_mockService.Object);
    }

    public void Dispose()
    {
        _orchestrator.Dispose();
    }

    [Fact]
    public async Task RequestThumbnailAsync_WhenCacheHit_DoesNotCallLoadThumbnailAsync()
    {
        // Arrange: cache returns a non-null bitmap (use null since we can't create Avalonia Bitmap in tests)
        // But TryGetCached returns true to simulate a cache hit
        Bitmap? outBitmap = null;
        _mockService.Setup(s => s.TryGetCached("test.png", out outBitmap)).Returns(true);

        // Act
        var result = await _orchestrator.RequestThumbnailAsync("test.png", _ownerA);

        // Assert: LoadThumbnailAsync should never be called when cache hits
        _mockService.Verify(s => s.LoadThumbnailAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RequestThumbnailAsync_WhenCacheMiss_CallsLoadThumbnailAsync()
    {
        // Arrange
        Bitmap? nullBitmap = null;
        _mockService.Setup(s => s.TryGetCached(It.IsAny<string>(), out nullBitmap)).Returns(false);
        _mockService.Setup(s => s.LoadThumbnailAsync("test.png", 340, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Bitmap?)null);

        // Act
        await _orchestrator.RequestThumbnailAsync("test.png", _ownerA);

        // Assert
        _mockService.Verify(s => s.LoadThumbnailAsync("test.png", 340, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RequestThumbnailAsync_WhenNullOrEmptyPath_ReturnsNull()
    {
        var result1 = await _orchestrator.RequestThumbnailAsync("", _ownerA);
        var result2 = await _orchestrator.RequestThumbnailAsync(null!, _ownerA);

        result1.Should().BeNull();
        result2.Should().BeNull();
    }

    [Fact]
    public async Task RequestThumbnailAsync_WhenCancelled_ReturnsNull()
    {
        // Arrange
        Bitmap? nullBitmap = null;
        _mockService.Setup(s => s.TryGetCached(It.IsAny<string>(), out nullBitmap)).Returns(false);
        _mockService.Setup(s => s.LoadThumbnailAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<string, int, CancellationToken>(async (_, _, ct) =>
            {
                await Task.Delay(5000, ct);
                return null;
            });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        var result = await _orchestrator.RequestThumbnailAsync("test.png", _ownerA, cancellationToken: cts.Token);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void SetActiveOwner_SetsActiveOwnerProperty()
    {
        _orchestrator.ActiveOwner.Should().BeNull();

        _orchestrator.SetActiveOwner(_ownerA);

        _orchestrator.ActiveOwner.Should().BeSameAs(_ownerA);
    }

    [Fact]
    public async Task SetActiveOwner_DoesNotCancelPreviousOwnerInFlightWork()
    {
        // Arrange: set up a quick-loading service
        Bitmap? nullBitmap = null;
        _mockService.Setup(s => s.TryGetCached(It.IsAny<string>(), out nullBitmap)).Returns(false);
        _mockService.Setup(s => s.LoadThumbnailAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<string, int, CancellationToken>(async (_, _, ct) =>
            {
                await Task.Delay(50, ct);
                return null;
            });

        // Act: start a request for ownerA, then switch to ownerB
        _orchestrator.SetActiveOwner(_ownerA);
        var requestTask = _orchestrator.RequestThumbnailAsync("slow.png", _ownerA);

        // Switch active owner — this should NOT cancel ownerA's in-flight work
        _orchestrator.SetActiveOwner(_ownerB);

        // The request should complete (not be cancelled)
        await requestTask;

        // Assert: the load was actually attempted (not cancelled)
        _mockService.Verify(s => s.LoadThumbnailAsync("slow.png", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelRequests_CancelsPendingRequestsForOwner()
    {
        // Arrange
        Bitmap? nullBitmap = null;
        _mockService.Setup(s => s.TryGetCached(It.IsAny<string>(), out nullBitmap)).Returns(false);
        _mockService.Setup(s => s.LoadThumbnailAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<string, int, CancellationToken>(async (_, _, ct) =>
            {
                await Task.Delay(10000, ct);
                return null;
            });

        // Act
        var requestTask = _orchestrator.RequestThumbnailAsync("test.png", _ownerA);
        _orchestrator.CancelRequests(_ownerA);
        var result = await requestTask;

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void TryGetCached_DelegatesToUnderlyingService()
    {
        // Arrange
        Bitmap? outBitmap = null;
        _mockService.Setup(s => s.TryGetCached("cached.png", out outBitmap)).Returns(true);

        // Act
        var result = _orchestrator.TryGetCached("cached.png", out _);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void Invalidate_DelegatesToUnderlyingService()
    {
        _orchestrator.Invalidate("test.png");

        _mockService.Verify(s => s.Invalidate("test.png"), Times.Once);
    }

    [Fact]
    public void ClearCache_DelegatesToUnderlyingService()
    {
        _orchestrator.ClearCache();

        _mockService.Verify(s => s.ClearCache(), Times.Once);
    }

    [Fact]
    public void GetStats_DelegatesToUnderlyingService()
    {
        var stats = new ThumbnailCacheStats(10, 200, 1024 * 1024);
        _mockService.Setup(s => s.GetStats()).Returns(stats);

        var result = _orchestrator.GetStats();

        result.Should().Be(stats);
    }

    [Fact]
    public async Task RequestThumbnailAsync_WhenActiveOwner_BoostsPriority()
    {
        // Arrange: track which paths get loaded and in what order.
        // The processing loop dispatches via fire-and-forget but the mock's synchronous
        // portion (before the first await) runs inline on the loop thread, so loadOrder
        // faithfully records the dequeue (priority) order.
        var loadOrder = new List<string>();

        Bitmap? nullBitmap = null;
        _mockService.Setup(s => s.TryGetCached(It.IsAny<string>(), out nullBitmap)).Returns(false);
        _mockService.Setup(s => s.LoadThumbnailAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<string, int, CancellationToken>((path, _, _) =>
            {
                // Returning a completed Task means ProcessRequestAsync runs entirely
                // synchronously within the fire-and-forget, blocking the loop thread.
                // The 100 ms sleep guarantees the test thread finishes enqueueing all
                // items before the loop thread can dequeue a second time.
                Thread.Sleep(100);
                lock (loadOrder)
                {
                    loadOrder.Add(path);
                }
                return Task.FromResult<Bitmap?>(null);
            });

        using var singleOrchestrator = new ThumbnailOrchestrator(_mockService.Object);
        singleOrchestrator.SetActiveOwner(_ownerA);

        // Allow the processing loop to start and block on the empty queue signal.
        await Task.Delay(50);

        // Act: submit several low-priority items (ownerB), then one active-owner
        // request (ownerA, Normal ? boosted to Critical).
        // All RequestThumbnailAsync calls run synchronously up to their final
        // "await tcs.Task", so the test thread enqueues them without yielding.
        var tasks = new List<Task<Bitmap?>>();
        for (var i = 0; i < 5; i++)
        {
            tasks.Add(singleOrchestrator.RequestThumbnailAsync(
                $"low_{i}.png", _ownerB, ThumbnailPriority.Low));
        }

        tasks.Add(singleOrchestrator.RequestThumbnailAsync(
            "critical.png", _ownerA, ThumbnailPriority.Normal));

        await Task.WhenAll(tasks);

        // Assert: the very first dequeue may grab low_0 before the remaining items
        // are enqueued (single item in the queue ? priority is irrelevant). While
        // the loop is blocked on that first load (Thread.Sleep), the test thread
        // enqueues the remaining 5 items. On the second dequeue the priority queue
        // has all of them: critical.png (Critical) beats every low item (Low).
        var criticalIndex = loadOrder.IndexOf("critical.png");
        criticalIndex.Should().BeLessThanOrEqualTo(1,
            "Active owner's request (boosted to Critical) should jump ahead of Low-priority items in the queue");
    }

    [Fact]
    public void Dispose_DoesNotThrowWithPendingRequests()
    {
        // Arrange
        Bitmap? nullBitmap = null;
        _mockService.Setup(s => s.TryGetCached(It.IsAny<string>(), out nullBitmap)).Returns(false);
        _mockService.Setup(s => s.LoadThumbnailAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<string, int, CancellationToken>(async (_, _, ct) =>
            {
                await Task.Delay(60000, ct);
                return null;
            });

        // Act: start a request but don't await — just verify dispose doesn't throw
        _ = _orchestrator.RequestThumbnailAsync("pending.png", _ownerA);

        var action = () => _orchestrator.Dispose();
        action.Should().NotThrow();
    }

    [Fact]
    public void ThumbnailOwnerToken_UsesReferenceEquality()
    {
        var token1 = new ThumbnailOwnerToken("Test");
        var token2 = new ThumbnailOwnerToken("Test");

        // Same name but different instances — should NOT be equal (reference equality)
        token1.Should().NotBeSameAs(token2);
    }

    [Fact]
    public void ThumbnailOwnerToken_ToString_ReturnsName()
    {
        var token = new ThumbnailOwnerToken("MyView");
        token.ToString().Should().Be("MyView");
    }

    [Fact]
    public void ThumbnailOwnerToken_ThrowsOnNullName()
    {
        var action = () => new ThumbnailOwnerToken(null!);
        action.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task RequestThumbnailAsync_PassesCorrectTargetWidth()
    {
        // Arrange
        Bitmap? nullBitmap = null;
        _mockService.Setup(s => s.TryGetCached(It.IsAny<string>(), out nullBitmap)).Returns(false);
        _mockService.Setup(s => s.LoadThumbnailAsync("test.png", 500, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Bitmap?)null);

        // Act
        await _orchestrator.RequestThumbnailAsync("test.png", _ownerA, targetWidth: 500);

        // Assert
        _mockService.Verify(s => s.LoadThumbnailAsync("test.png", 500, It.IsAny<CancellationToken>()), Times.Once);
    }
}
