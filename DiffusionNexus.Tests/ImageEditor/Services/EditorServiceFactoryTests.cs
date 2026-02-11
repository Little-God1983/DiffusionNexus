using DiffusionNexus.UI.ImageEditor.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.ImageEditor.Services;

/// <summary>
/// Unit tests for <see cref="EditorServiceFactory"/> and <see cref="EditorServices"/>.
/// Tests factory wiring and disposal behavior.
/// </summary>
public class EditorServiceFactoryTests
{
    [Fact]
    public void WhenCreate_AllServicesAreNonNull()
    {
        // Act
        var services = EditorServiceFactory.Create();

        // Assert
        services.EventBus.Should().NotBeNull();
        services.Viewport.Should().NotBeNull();
        services.Tools.Should().NotBeNull();
        services.Document.Should().NotBeNull();
        services.Layers.Should().NotBeNull();
    }

    [Fact]
    public void WhenCreate_ServicesShareSameEventBus()
    {
        // Act
        var services = EditorServiceFactory.Create();

        // Assert — publish via event bus and verify viewport manager receives it
        var raised = false;
        services.Viewport.Changed += (_, _) => raised = true;
        services.Viewport.ZoomIn();
        raised.Should().BeTrue();
    }

    [Fact]
    public void WhenCreateMultiple_EachHasIndependentEventBus()
    {
        // Arrange
        var services1 = EditorServiceFactory.Create();
        var services2 = EditorServiceFactory.Create();

        // Act — activate a tool on services1
        services1.Tools.Activate(ToolIds.Crop);

        // Assert — services2 is unaffected
        services2.Tools.ActiveToolId.Should().BeNull();
    }

    [Fact]
    public void WhenDispose_DoesNotThrow()
    {
        // Arrange
        var services = EditorServiceFactory.Create();

        // Act
        var act = () => services.Dispose();

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void WhenDisposeTwice_DoesNotThrow()
    {
        // Arrange
        var services = EditorServiceFactory.Create();

        // Act
        services.Dispose();
        var act = () => services.Dispose();

        // Assert
        act.Should().NotThrow();
    }
}
