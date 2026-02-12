using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.ImageEditor.Services;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor.Services;

/// <summary>
/// Unit tests for <see cref="ILayerManager"/> implementation.
/// Tests enable/disable layer mode, CRUD operations, move, merge, and event publishing.
/// </summary>
public class LayerManagerTests : IDisposable
{
    private readonly EditorServices _services;
    private readonly ILayerManager _sut;
    private readonly SKBitmap _testBitmap;

    public LayerManagerTests()
    {
        _services = EditorServiceFactory.Create();
        _sut = _services.Layers;
        _testBitmap = new SKBitmap(100, 100, SKColorType.Rgba8888, SKAlphaType.Premul);
        _testBitmap.Erase(SKColors.Red);
    }

    public void Dispose()
    {
        _services.Dispose();
        _testBitmap.Dispose();
    }

    #region Default State

    [Fact]
    public void WhenCreated_LayerModeIsDisabled()
    {
        _sut.IsLayerMode.Should().BeFalse();
        _sut.Stack.Should().BeNull();
        _sut.ActiveLayer.Should().BeNull();
        _sut.Count.Should().Be(0);
    }

    #endregion

    #region EnableLayerMode

    [Fact]
    public void WhenEnableLayerMode_StackIsCreated()
    {
        // Act
        _sut.EnableLayerMode(_testBitmap, "Background");

        // Assert
        _sut.IsLayerMode.Should().BeTrue();
        _sut.Stack.Should().NotBeNull();
        _sut.Count.Should().Be(1);
        _sut.ActiveLayer.Should().NotBeNull();
        _sut.ActiveLayer!.Name.Should().Be("Background");
    }

    [Fact]
    public void WhenEnableLayerMode_DimensionsMatchBitmap()
    {
        // Act
        _sut.EnableLayerMode(_testBitmap, "Background");

        // Assert
        _sut.Width.Should().Be(100);
        _sut.Height.Should().Be(100);
    }

    [Fact]
    public void WhenEnableLayerModeTwice_SecondCallIgnored()
    {
        // Arrange
        _sut.EnableLayerMode(_testBitmap, "Background");

        // Act
        using var anotherBitmap = new SKBitmap(200, 200);
        _sut.EnableLayerMode(anotherBitmap, "Second");

        // Assert — still has original dimensions
        _sut.Width.Should().Be(100);
        _sut.Count.Should().Be(1);
    }

    [Fact]
    public void WhenEnableLayerModeWithNullBitmap_ThrowsArgumentNullException()
    {
        var act = () => _sut.EnableLayerMode(null!, "Background");
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WhenEnableLayerMode_LayerModeChangedEventRaised()
    {
        // Arrange
        var raised = false;
        _sut.LayerModeChanged += (_, _) => raised = true;

        // Act
        _sut.EnableLayerMode(_testBitmap, "Background");

        // Assert
        raised.Should().BeTrue();
    }

    [Fact]
    public void WhenEnableLayerMode_LayersChangedEventRaised()
    {
        // Arrange
        var raised = false;
        _sut.LayersChanged += (_, _) => raised = true;

        // Act
        _sut.EnableLayerMode(_testBitmap, "Background");

        // Assert — LayerStack fires LayersChanged when a layer is added
        raised.Should().BeTrue();
    }

    #endregion

    #region DisableLayerMode

    [Fact]
    public void WhenDisableLayerMode_ReturnsFlattenedBitmap()
    {
        // Arrange
        _sut.EnableLayerMode(_testBitmap, "Background");

        // Act
        using var result = _sut.DisableLayerMode();

        // Assert
        result.Should().NotBeNull();
        result!.Width.Should().Be(100);
        result.Height.Should().Be(100);
        _sut.IsLayerMode.Should().BeFalse();
        _sut.Stack.Should().BeNull();
    }

    [Fact]
    public void WhenDisableLayerModeWithNoStack_ReturnsNull()
    {
        // Act
        var result = _sut.DisableLayerMode();

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Reset

    [Fact]
    public void WhenReset_StackIsDisposedWithoutFlatten()
    {
        // Arrange
        _sut.EnableLayerMode(_testBitmap, "Background");
        _sut.AddLayer("Layer 2");

        // Act
        _sut.Reset();

        // Assert
        _sut.IsLayerMode.Should().BeFalse();
        _sut.Stack.Should().BeNull();
        _sut.Count.Should().Be(0);
    }

    [Fact]
    public void WhenReset_LayerModeChangedEventRaised()
    {
        // Arrange
        _sut.EnableLayerMode(_testBitmap, "Background");
        var raised = false;
        _sut.LayerModeChanged += (_, _) => raised = true;

        // Act
        _sut.Reset();

        // Assert
        raised.Should().BeTrue();
    }

    [Fact]
    public void WhenResetWithNoStack_DoesNotThrow()
    {
        // Act
        var act = () => _sut.Reset();

        // Assert
        act.Should().NotThrow();
    }

    #endregion

    #region AddLayer

    [Fact]
    public void WhenAddLayer_LayerAddedToStack()
    {
        // Arrange
        _sut.EnableLayerMode(_testBitmap, "Background");

        // Act
        var layer = _sut.AddLayer("Layer 2");

        // Assert
        layer.Should().NotBeNull();
        _sut.Count.Should().Be(2);
        _sut.ActiveLayer.Should().Be(layer);
    }

    [Fact]
    public void WhenAddLayerWithoutLayerMode_ReturnsNull()
    {
        // Act
        var layer = _sut.AddLayer("Test");

        // Assert
        layer.Should().BeNull();
    }

    [Fact]
    public void WhenAddLayer_LayersChangedEventRaised()
    {
        // Arrange
        _sut.EnableLayerMode(_testBitmap, "Background");
        var raised = false;
        _sut.LayersChanged += (_, _) => raised = true;

        // Act
        _sut.AddLayer("Layer 2");

        // Assert
        raised.Should().BeTrue();
    }

    #endregion

    #region AddLayerFromBitmap

    [Fact]
    public void WhenAddLayerFromBitmap_LayerHasCorrectContent()
    {
        // Arrange
        _sut.EnableLayerMode(_testBitmap, "Background");
        using var greenBitmap = new SKBitmap(100, 100, SKColorType.Rgba8888, SKAlphaType.Premul);
        greenBitmap.Erase(SKColors.Green);

        // Act
        var layer = _sut.AddLayerFromBitmap(greenBitmap, "Green Layer");

        // Assert
        layer.Should().NotBeNull();
        layer!.Name.Should().Be("Green Layer");
        _sut.Count.Should().Be(2);
    }

    [Fact]
    public void WhenAddLayerFromBitmapWithoutLayerMode_ReturnsNull()
    {
        // Act
        var layer = _sut.AddLayerFromBitmap(_testBitmap, "Test");

        // Assert
        layer.Should().BeNull();
    }

    #endregion

    #region RemoveLayer

    [Fact]
    public void WhenRemoveLayer_LayerRemovedFromStack()
    {
        // Arrange
        _sut.EnableLayerMode(_testBitmap, "Background");
        var layer2 = _sut.AddLayer("Layer 2");

        // Act
        var removed = _sut.RemoveLayer(layer2!);

        // Assert
        removed.Should().BeTrue();
        _sut.Count.Should().Be(1);
    }

    [Fact]
    public void WhenRemoveLastLayer_ReturnsFalse()
    {
        // Arrange
        _sut.EnableLayerMode(_testBitmap, "Background");
        var onlyLayer = _sut.ActiveLayer!;

        // Act
        var removed = _sut.RemoveLayer(onlyLayer);

        // Assert
        removed.Should().BeFalse();
        _sut.Count.Should().Be(1);
    }

    [Fact]
    public void WhenRemoveLayer_LayersChangedEventRaised()
    {
        // Arrange
        _sut.EnableLayerMode(_testBitmap, "Background");
        var layer2 = _sut.AddLayer("Layer 2")!;
        var raised = false;
        _sut.LayersChanged += (_, _) => raised = true;

        // Act
        _sut.RemoveLayer(layer2);

        // Assert
        raised.Should().BeTrue();
    }

    #endregion

    #region DuplicateLayer

    [Fact]
    public void WhenDuplicateLayer_CloneAddedToStack()
    {
        // Arrange
        _sut.EnableLayerMode(_testBitmap, "Background");
        var original = _sut.ActiveLayer!;

        // Act
        var clone = _sut.DuplicateLayer(original);

        // Assert
        clone.Should().NotBeNull();
        clone!.Name.Should().Contain("Copy");
        _sut.Count.Should().Be(2);
        _sut.ActiveLayer.Should().Be(clone);
    }

    [Fact]
    public void WhenDuplicateLayerWithoutLayerMode_ReturnsNull()
    {
        // Arrange
        _sut.EnableLayerMode(_testBitmap, "Background");
        var layer = _sut.ActiveLayer!;
        _sut.DisableLayerMode()?.Dispose();

        // Act
        var clone = _sut.DuplicateLayer(layer);

        // Assert
        clone.Should().BeNull();
    }

    #endregion

    #region MoveLayer

    [Fact]
    public void WhenMoveLayerUp_LayerOrderChanges()
    {
        // Arrange
        _sut.EnableLayerMode(_testBitmap, "Background");
        _sut.AddLayer("Layer 2");
        var background = _sut.Stack![0];

        // Act
        var moved = _sut.MoveLayerUp(background);

        // Assert
        moved.Should().BeTrue();
    }

    [Fact]
    public void WhenMoveLayerUp_LayersChangedEventRaised()
    {
        // Arrange
        _sut.EnableLayerMode(_testBitmap, "Background");
        _sut.AddLayer("Layer 2");
        var background = _sut.Stack![0];
        var raised = false;
        _sut.LayersChanged += (_, _) => raised = true;

        // Act
        _sut.MoveLayerUp(background);

        // Assert
        raised.Should().BeTrue();
    }

    [Fact]
    public void WhenMoveTopLayerUp_ReturnsFalse()
    {
        // Arrange
        _sut.EnableLayerMode(_testBitmap, "Background");
        _sut.AddLayer("Layer 2");
        var topLayer = _sut.Stack![_sut.Count - 1];

        // Act
        var moved = _sut.MoveLayerUp(topLayer);

        // Assert
        moved.Should().BeFalse();
    }

    [Fact]
    public void WhenMoveLayerDown_LayerOrderChanges()
    {
        // Arrange
        _sut.EnableLayerMode(_testBitmap, "Background");
        var layer2 = _sut.AddLayer("Layer 2")!;

        // Act
        var moved = _sut.MoveLayerDown(layer2);

        // Assert
        moved.Should().BeTrue();
    }

    [Fact]
    public void WhenMoveBottomLayerDown_ReturnsFalse()
    {
        // Arrange
        _sut.EnableLayerMode(_testBitmap, "Background");
        _sut.AddLayer("Layer 2");
        var background = _sut.Stack![0];

        // Act
        var moved = _sut.MoveLayerDown(background);

        // Assert
        moved.Should().BeFalse();
    }

    #endregion

    #region MergeLayerDown

    [Fact]
    public void WhenMergeLayerDown_LayerCountDecreases()
    {
        // Arrange
        _sut.EnableLayerMode(_testBitmap, "Background");
        var layer2 = _sut.AddLayer("Layer 2")!;

        // Act
        var merged = _sut.MergeLayerDown(layer2);

        // Assert
        merged.Should().BeTrue();
        _sut.Count.Should().Be(1);
    }

    [Fact]
    public void WhenMergeLayerDown_LayersChangedEventRaised()
    {
        // Arrange
        _sut.EnableLayerMode(_testBitmap, "Background");
        var layer2 = _sut.AddLayer("Layer 2")!;
        var raised = false;
        _sut.LayersChanged += (_, _) => raised = true;

        // Act
        _sut.MergeLayerDown(layer2);

        // Assert
        raised.Should().BeTrue();
    }

    [Fact]
    public void WhenMergeBottomLayerDown_ReturnsFalse()
    {
        // Arrange
        _sut.EnableLayerMode(_testBitmap, "Background");
        var background = _sut.Stack![0];

        // Act
        var merged = _sut.MergeLayerDown(background);

        // Assert
        merged.Should().BeFalse();
    }

    #endregion

    #region MergeVisibleLayers

    [Fact]
    public void WhenMergeVisibleLayers_ResultsInSingleLayer()
    {
        // Arrange
        _sut.EnableLayerMode(_testBitmap, "Background");
        _sut.AddLayer("Layer 2");
        _sut.AddLayer("Layer 3");

        // Act
        _sut.MergeVisibleLayers();

        // Assert
        _sut.Count.Should().Be(1);
        _sut.ActiveLayer!.Name.Should().Be("Merged");
    }

    [Fact]
    public void WhenMergeVisibleLayers_LayersChangedEventRaised()
    {
        // Arrange
        _sut.EnableLayerMode(_testBitmap, "Background");
        _sut.AddLayer("Layer 2");
        var raised = false;
        _sut.LayersChanged += (_, _) => raised = true;

        // Act
        _sut.MergeVisibleLayers();

        // Assert
        raised.Should().BeTrue();
    }

    #endregion

    #region Flatten

    [Fact]
    public void WhenFlatten_ReturnsBitmapWithoutModifyingStack()
    {
        // Arrange
        _sut.EnableLayerMode(_testBitmap, "Background");
        _sut.AddLayer("Layer 2");

        // Act
        using var flattened = _sut.Flatten();

        // Assert
        flattened.Should().NotBeNull();
        flattened!.Width.Should().Be(100);
        flattened.Height.Should().Be(100);
        _sut.Count.Should().Be(2); // stack unchanged
    }

    [Fact]
    public void WhenFlattenWithNoStack_ReturnsNull()
    {
        // Act
        var result = _sut.Flatten();

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region FlattenAllLayers

    [Fact]
    public void WhenFlattenAllLayers_ResultsInSingleLayer()
    {
        // Arrange
        _sut.EnableLayerMode(_testBitmap, "Background");
        _sut.AddLayer("Layer 2");
        _sut.AddLayer("Layer 3");

        // Act
        _sut.FlattenAllLayers();

        // Assert
        _sut.Count.Should().Be(1);
        _sut.ActiveLayer.Should().NotBeNull();
    }

    [Fact]
    public void WhenFlattenAllLayers_EventPublished()
    {
        // Arrange
        _sut.EnableLayerMode(_testBitmap, "Background");
        _sut.AddLayer("Layer 2");
        var layersRaised = false;
        var contentRaised = false;
        _sut.LayersChanged += (_, _) => layersRaised = true;
        _sut.ContentChanged += (_, _) => contentRaised = true;

        // Act
        _sut.FlattenAllLayers();

        // Assert
        layersRaised.Should().BeTrue();
        contentRaised.Should().BeTrue();
    }

    #endregion

    #region ActiveLayer

    [Fact]
    public void WhenSetActiveLayer_ActiveLayerChanges()
    {
        // Arrange
        _sut.EnableLayerMode(_testBitmap, "Background");
        var layer2 = _sut.AddLayer("Layer 2")!;

        // Set active to background
        var background = _sut.Stack![0];

        // Act
        _sut.ActiveLayer = background;

        // Assert
        _sut.ActiveLayer.Should().Be(background);
    }

    #endregion

    #region ContentChanged Event

    [Fact]
    public void WhenLayerContentChanges_ContentChangedEventRaised()
    {
        // Arrange
        _sut.EnableLayerMode(_testBitmap, "Background");
        var raised = false;
        _sut.ContentChanged += (_, _) => raised = true;

        // Act — modify the layer content
        _sut.ActiveLayer!.NotifyContentChanged();

        // Assert
        raised.Should().BeTrue();
    }

    #endregion
}
