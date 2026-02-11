using DiffusionNexus.UI.ImageEditor.Events;
using DiffusionNexus.UI.ImageEditor.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.ImageEditor.Services;

/// <summary>
/// Unit tests for <see cref="IViewportManager"/> implementation.
/// Tests zoom, pan, fit mode, reset, clamping, and event bus integration.
/// </summary>
public class ViewportManagerTests
{
    private readonly EditorServices _services;
    private readonly IViewportManager _sut;

    public ViewportManagerTests()
    {
        _services = EditorServiceFactory.Create();
        _sut = _services.Viewport;
    }

    #region Default State

    [Fact]
    public void WhenCreated_HasDefaultState()
    {
        _sut.ZoomLevel.Should().Be(1f);
        _sut.ZoomPercentage.Should().Be(100);
        _sut.PanX.Should().Be(0f);
        _sut.PanY.Should().Be(0f);
        _sut.IsFitMode.Should().BeTrue();
    }

    #endregion

    #region ZoomIn / ZoomOut

    [Fact]
    public void WhenZoomIn_ZoomLevelIncreases()
    {
        // Act
        _sut.ZoomIn();

        // Assert
        _sut.ZoomLevel.Should().BeApproximately(1.1f, 0.001f);
        _sut.ZoomPercentage.Should().Be(110);
    }

    [Fact]
    public void WhenZoomOut_ZoomLevelDecreases()
    {
        // Arrange — start at 1.0 (default)
        _sut.ZoomLevel = 1f;

        // Act
        _sut.ZoomOut();

        // Assert
        _sut.ZoomLevel.Should().BeApproximately(0.9f, 0.001f);
    }

    [Fact]
    public void WhenZoomInBeyondMax_ClampedToMax()
    {
        // Act
        _sut.ZoomLevel = 100f;

        // Assert
        _sut.ZoomLevel.Should().Be(_sut.MaxZoom);
    }

    [Fact]
    public void WhenZoomOutBelowMin_ClampedToMin()
    {
        // Act
        _sut.ZoomLevel = 0.001f;

        // Assert
        _sut.ZoomLevel.Should().Be(_sut.MinZoom);
    }

    [Fact]
    public void WhenZoomLevelSet_FitModeDisabled()
    {
        // Arrange
        _sut.IsFitMode.Should().BeTrue();

        // Act
        _sut.ZoomLevel = 2f;

        // Assert
        _sut.IsFitMode.Should().BeFalse();
    }

    #endregion

    #region ZoomToFit / ZoomToActual

    [Fact]
    public void WhenZoomToFit_FitModeEnabled()
    {
        // Arrange
        _sut.ZoomLevel = 2f;
        _sut.IsFitMode.Should().BeFalse();

        // Act
        _sut.ZoomToFit();

        // Assert
        _sut.IsFitMode.Should().BeTrue();
    }

    [Fact]
    public void WhenZoomToActual_ZoomIsOneAndFitModeDisabled()
    {
        // Arrange
        _sut.ZoomLevel = 3f;

        // Act
        _sut.ZoomToActual();

        // Assert
        _sut.ZoomLevel.Should().Be(1f);
        _sut.ZoomPercentage.Should().Be(100);
        _sut.IsFitMode.Should().BeFalse();
        _sut.PanX.Should().Be(0f);
        _sut.PanY.Should().Be(0f);
    }

    #endregion

    #region Pan

    [Fact]
    public void WhenPanInNonFitMode_OffsetUpdated()
    {
        // Arrange
        _sut.ZoomLevel = 2f; // exits fit mode

        // Act
        _sut.Pan(10f, 20f);

        // Assert
        _sut.PanX.Should().Be(10f);
        _sut.PanY.Should().Be(20f);
    }

    [Fact]
    public void WhenPanInFitMode_OffsetNotChanged()
    {
        // Arrange — fit mode is default
        _sut.IsFitMode.Should().BeTrue();

        // Act
        _sut.Pan(10f, 20f);

        // Assert
        _sut.PanX.Should().Be(0f);
        _sut.PanY.Should().Be(0f);
    }

    [Fact]
    public void WhenPanMultipleTimes_OffsetsAccumulate()
    {
        // Arrange
        _sut.ZoomLevel = 2f;

        // Act
        _sut.Pan(5f, 10f);
        _sut.Pan(3f, -5f);

        // Assert
        _sut.PanX.Should().Be(8f);
        _sut.PanY.Should().Be(5f);
    }

    #endregion

    #region FitMode

    [Fact]
    public void WhenFitModeEnabled_PanResets()
    {
        // Arrange
        _sut.ZoomLevel = 2f;
        _sut.Pan(50f, 100f);

        // Act
        _sut.IsFitMode = true;

        // Assert
        _sut.PanX.Should().Be(0f);
        _sut.PanY.Should().Be(0f);
    }

    [Fact]
    public void WhenSetFitModeWithZoom_ZoomAndFitModeSet()
    {
        // Act
        _sut.SetFitModeWithZoom(0.5f);

        // Assert
        _sut.ZoomLevel.Should().BeApproximately(0.5f, 0.001f);
        _sut.IsFitMode.Should().BeTrue();
        _sut.PanX.Should().Be(0f);
        _sut.PanY.Should().Be(0f);
    }

    [Fact]
    public void WhenSetFitModeWithZoomBeyondMax_ClampedToMax()
    {
        // Act
        _sut.SetFitModeWithZoom(50f);

        // Assert
        _sut.ZoomLevel.Should().Be(_sut.MaxZoom);
        _sut.IsFitMode.Should().BeTrue();
    }

    #endregion

    #region Reset

    [Fact]
    public void WhenReset_AllStateReturnsToDefaults()
    {
        // Arrange
        _sut.ZoomLevel = 3f;
        _sut.Pan(50f, 100f);

        // Act
        _sut.Reset();

        // Assert
        _sut.ZoomLevel.Should().Be(1f);
        _sut.PanX.Should().Be(0f);
        _sut.PanY.Should().Be(0f);
        _sut.IsFitMode.Should().BeTrue();
    }

    #endregion

    #region Changed Event

    [Fact]
    public void WhenZoomLevelChanges_ChangedEventRaised()
    {
        // Arrange
        var raised = false;
        _sut.Changed += (_, _) => raised = true;

        // Act
        _sut.ZoomLevel = 2f;

        // Assert
        raised.Should().BeTrue();
    }

    [Fact]
    public void WhenZoomToFit_ChangedEventRaised()
    {
        // Arrange
        _sut.ZoomLevel = 2f; // exit fit mode first
        var raised = false;
        _sut.Changed += (_, _) => raised = true;

        // Act
        _sut.ZoomToFit();

        // Assert
        raised.Should().BeTrue();
    }

    [Fact]
    public void WhenZoomLevelSetToSameValue_ChangedEventNotRaised()
    {
        // Arrange
        _sut.ZoomLevel = 2f;
        var raised = false;
        _sut.Changed += (_, _) => raised = true;

        // Act
        _sut.ZoomLevel = 2f;

        // Assert
        raised.Should().BeFalse();
    }

    #endregion

    #region EventBus Integration

    [Fact]
    public void WhenZoomChanges_ViewportChangedEventPublishedOnBus()
    {
        // Arrange
        ViewportChangedEvent? received = null;
        _services.EventBus.Subscribe<ViewportChangedEvent>(e => received = e);

        // Act
        _sut.ZoomLevel = 2.5f;

        // Assert
        received.Should().NotBeNull();
        received!.ZoomLevel.Should().BeApproximately(2.5f, 0.001f);
        received.IsFitMode.Should().BeFalse();
    }

    [Fact]
    public void WhenReset_ViewportChangedEventPublishedOnBus()
    {
        // Arrange
        ViewportChangedEvent? received = null;
        _services.EventBus.Subscribe<ViewportChangedEvent>(e => received = e);

        // Act
        _sut.Reset();

        // Assert
        received.Should().NotBeNull();
        received!.ZoomLevel.Should().Be(1f);
        received.IsFitMode.Should().BeTrue();
    }

    #endregion
}
