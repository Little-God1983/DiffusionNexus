using DiffusionNexus.UI.ImageEditor.Events;
using DiffusionNexus.UI.ImageEditor.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.ImageEditor.Events;

/// <summary>
/// Unit tests for <see cref="IEventBus"/> implementation.
/// Tests publish/subscribe, unsubscribe, multi-subscriber, and type isolation.
/// </summary>
public class EventBusTests
{
    private readonly IEventBus _sut;

    public EventBusTests()
    {
        // Use the factory to get a properly wired event bus (internal impl)
        var services = EditorServiceFactory.Create();
        _sut = services.EventBus;
    }

    #region Publish / Subscribe

    [Fact]
    public void WhenSubscribedThenPublish_HandlerReceivesEvent()
    {
        // Arrange
        ViewportChangedEvent? received = null;
        _sut.Subscribe<ViewportChangedEvent>(e => received = e);

        var evt = new ViewportChangedEvent(2f, false, 10f, 20f);

        // Act
        _sut.Publish(evt);

        // Assert
        received.Should().NotBeNull();
        received!.ZoomLevel.Should().Be(2f);
        received.IsFitMode.Should().BeFalse();
        received.PanX.Should().Be(10f);
        received.PanY.Should().Be(20f);
    }

    [Fact]
    public void WhenNoSubscribers_PublishDoesNotThrow()
    {
        // Arrange
        var evt = new RenderRequestedEvent();

        // Act
        var act = () => _sut.Publish(evt);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void WhenMultipleSubscribers_AllReceiveEvent()
    {
        // Arrange
        var received1 = false;
        var received2 = false;
        var received3 = false;

        _sut.Subscribe<RenderRequestedEvent>(_ => received1 = true);
        _sut.Subscribe<RenderRequestedEvent>(_ => received2 = true);
        _sut.Subscribe<RenderRequestedEvent>(_ => received3 = true);

        // Act
        _sut.Publish(new RenderRequestedEvent());

        // Assert
        received1.Should().BeTrue();
        received2.Should().BeTrue();
        received3.Should().BeTrue();
    }

    #endregion

    #region Unsubscribe

    [Fact]
    public void WhenDisposed_HandlerNoLongerReceivesEvents()
    {
        // Arrange
        var callCount = 0;
        var subscription = _sut.Subscribe<RenderRequestedEvent>(_ => callCount++);

        _sut.Publish(new RenderRequestedEvent());
        callCount.Should().Be(1);

        // Act
        subscription.Dispose();
        _sut.Publish(new RenderRequestedEvent());

        // Assert
        callCount.Should().Be(1);
    }

    [Fact]
    public void WhenDisposedTwice_DoesNotThrow()
    {
        // Arrange
        var subscription = _sut.Subscribe<RenderRequestedEvent>(_ => { });

        // Act
        subscription.Dispose();
        var act = () => subscription.Dispose();

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void WhenOneSubscriberDisposed_OtherStillReceives()
    {
        // Arrange
        var received1 = 0;
        var received2 = 0;

        var sub1 = _sut.Subscribe<RenderRequestedEvent>(_ => received1++);
        _sut.Subscribe<RenderRequestedEvent>(_ => received2++);

        // Act
        sub1.Dispose();
        _sut.Publish(new RenderRequestedEvent());

        // Assert
        received1.Should().Be(0);
        received2.Should().Be(1);
    }

    #endregion

    #region Type Isolation

    [Fact]
    public void WhenDifferentEventTypes_SubscribersAreIsolated()
    {
        // Arrange
        var viewportReceived = false;
        var renderReceived = false;

        _sut.Subscribe<ViewportChangedEvent>(_ => viewportReceived = true);
        _sut.Subscribe<RenderRequestedEvent>(_ => renderReceived = true);

        // Act — only publish RenderRequestedEvent
        _sut.Publish(new RenderRequestedEvent());

        // Assert
        viewportReceived.Should().BeFalse();
        renderReceived.Should().BeTrue();
    }

    [Fact]
    public void WhenSubscribeWithNullHandler_ThrowsArgumentNullException()
    {
        // Act
        var act = () => _sut.Subscribe<RenderRequestedEvent>(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region Event Data Integrity

    [Fact]
    public void WhenPublishLayerStackChangedEvent_DataIsPreserved()
    {
        // Arrange
        LayerStackChangedEvent? received = null;
        _sut.Subscribe<LayerStackChangedEvent>(e => received = e);

        // Act
        _sut.Publish(new LayerStackChangedEvent(LayerChangeType.Added, null));

        // Assert
        received.Should().NotBeNull();
        received!.ChangeType.Should().Be(LayerChangeType.Added);
        received.AffectedLayer.Should().BeNull();
    }

    [Fact]
    public void WhenPublishToolChangedEvent_DataIsPreserved()
    {
        // Arrange
        ToolChangedEvent? received = null;
        _sut.Subscribe<ToolChangedEvent>(e => received = e);

        // Act
        _sut.Publish(new ToolChangedEvent("OldTool", "NewTool"));

        // Assert
        received.Should().NotBeNull();
        received!.OldToolId.Should().Be("OldTool");
        received.NewToolId.Should().Be("NewTool");
    }

    #endregion
}
