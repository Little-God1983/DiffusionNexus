using DiffusionNexus.UI.ImageEditor.Events;
using DiffusionNexus.UI.ImageEditor.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.ImageEditor.Services;

/// <summary>
/// Unit tests for <see cref="IToolManager"/> implementation.
/// Tests activation, deactivation, toggle, mutual exclusion, callbacks, and events.
/// </summary>
public class ToolManagerTests
{
    private readonly EditorServices _services;
    private readonly IToolManager _sut;

    public ToolManagerTests()
    {
        _services = EditorServiceFactory.Create();
        _sut = _services.Tools;
    }

    #region Default State

    [Fact]
    public void WhenCreated_NoToolIsActive()
    {
        _sut.ActiveToolId.Should().BeNull();
    }

    #endregion

    #region Activate

    [Fact]
    public void WhenActivateTool_ToolBecomesActive()
    {
        // Act
        _sut.Activate(ToolIds.Crop);

        // Assert
        _sut.ActiveToolId.Should().Be(ToolIds.Crop);
        _sut.IsActive(ToolIds.Crop).Should().BeTrue();
    }

    [Fact]
    public void WhenActivateSameToolTwice_NoChange()
    {
        // Arrange
        _sut.Activate(ToolIds.Crop);
        var eventCount = 0;
        _sut.ActiveToolChanged += (_, _) => eventCount++;

        // Act
        _sut.Activate(ToolIds.Crop);

        // Assert
        eventCount.Should().Be(0);
        _sut.ActiveToolId.Should().Be(ToolIds.Crop);
    }

    [Fact]
    public void WhenActivateNullToolId_ThrowsArgumentException()
    {
        var act = () => _sut.Activate(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WhenActivateWhitespaceToolId_ThrowsArgumentException()
    {
        var act = () => _sut.Activate("   ");
        act.Should().Throw<ArgumentException>();
    }

    #endregion

    #region Mutual Exclusion

    [Fact]
    public void WhenActivateNewTool_PreviousToolDeactivated()
    {
        // Arrange
        _sut.Activate(ToolIds.Crop);

        // Act
        _sut.Activate(ToolIds.Drawing);

        // Assert
        _sut.ActiveToolId.Should().Be(ToolIds.Drawing);
        _sut.IsActive(ToolIds.Crop).Should().BeFalse();
        _sut.IsActive(ToolIds.Drawing).Should().BeTrue();
    }

    [Fact]
    public void WhenActivateNewTool_PreviousDeactivationCallbackInvoked()
    {
        // Arrange
        var cropDeactivated = false;
        _sut.RegisterDeactivationCallback(ToolIds.Crop, () => cropDeactivated = true);
        _sut.Activate(ToolIds.Crop);

        // Act
        _sut.Activate(ToolIds.Drawing);

        // Assert
        cropDeactivated.Should().BeTrue();
    }

    [Fact]
    public void WhenActivateThreeToolsInSequence_OnlyLastIsActive()
    {
        // Act
        _sut.Activate(ToolIds.Crop);
        _sut.Activate(ToolIds.ColorBalance);
        _sut.Activate(ToolIds.Drawing);

        // Assert
        _sut.ActiveToolId.Should().Be(ToolIds.Drawing);
        _sut.IsActive(ToolIds.Crop).Should().BeFalse();
        _sut.IsActive(ToolIds.ColorBalance).Should().BeFalse();
    }

    #endregion

    #region Deactivate

    [Fact]
    public void WhenDeactivateActiveTool_NoToolIsActive()
    {
        // Arrange
        _sut.Activate(ToolIds.Crop);

        // Act
        _sut.Deactivate(ToolIds.Crop);

        // Assert
        _sut.ActiveToolId.Should().BeNull();
        _sut.IsActive(ToolIds.Crop).Should().BeFalse();
    }

    [Fact]
    public void WhenDeactivateInactiveTool_NoChange()
    {
        // Arrange
        _sut.Activate(ToolIds.Crop);
        var eventCount = 0;
        _sut.ActiveToolChanged += (_, _) => eventCount++;

        // Act
        _sut.Deactivate(ToolIds.Drawing);

        // Assert
        _sut.ActiveToolId.Should().Be(ToolIds.Crop);
        eventCount.Should().Be(0);
    }

    [Fact]
    public void WhenDeactivateNullToolId_ThrowsArgumentException()
    {
        var act = () => _sut.Deactivate(null!);
        act.Should().Throw<ArgumentException>();
    }

    #endregion

    #region Toggle

    [Fact]
    public void WhenToggleInactiveTool_ToolBecomesActive()
    {
        // Act
        _sut.Toggle(ToolIds.Crop);

        // Assert
        _sut.ActiveToolId.Should().Be(ToolIds.Crop);
    }

    [Fact]
    public void WhenToggleActiveTool_ToolBecomesInactive()
    {
        // Arrange
        _sut.Activate(ToolIds.Crop);

        // Act
        _sut.Toggle(ToolIds.Crop);

        // Assert
        _sut.ActiveToolId.Should().BeNull();
    }

    [Fact]
    public void WhenToggleDifferentTool_SwitchesToNewTool()
    {
        // Arrange
        _sut.Activate(ToolIds.Crop);

        // Act
        _sut.Toggle(ToolIds.Drawing);

        // Assert
        _sut.ActiveToolId.Should().Be(ToolIds.Drawing);
    }

    #endregion

    #region DeactivateAll

    [Fact]
    public void WhenDeactivateAllWithActiveTool_NoToolIsActive()
    {
        // Arrange
        _sut.Activate(ToolIds.Drawing);

        // Act
        _sut.DeactivateAll();

        // Assert
        _sut.ActiveToolId.Should().BeNull();
    }

    [Fact]
    public void WhenDeactivateAllWithNoActiveTool_NoChange()
    {
        // Arrange
        var eventCount = 0;
        _sut.ActiveToolChanged += (_, _) => eventCount++;

        // Act
        _sut.DeactivateAll();

        // Assert
        eventCount.Should().Be(0);
    }

    #endregion

    #region Deactivation Callbacks

    [Fact]
    public void WhenDeactivateTool_CallbackInvoked()
    {
        // Arrange
        var callbackInvoked = false;
        _sut.RegisterDeactivationCallback(ToolIds.Crop, () => callbackInvoked = true);
        _sut.Activate(ToolIds.Crop);

        // Act
        _sut.Deactivate(ToolIds.Crop);

        // Assert
        callbackInvoked.Should().BeTrue();
    }

    [Fact]
    public void WhenToolWithoutCallback_NoException()
    {
        // Arrange
        _sut.Activate(ToolIds.Drawing);

        // Act
        var act = () => _sut.Deactivate(ToolIds.Drawing);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void WhenRegisterCallbackWithNullToolId_ThrowsArgumentException()
    {
        var act = () => _sut.RegisterDeactivationCallback(null!, () => { });
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WhenRegisterCallbackWithNullAction_ThrowsArgumentNullException()
    {
        var act = () => _sut.RegisterDeactivationCallback(ToolIds.Crop, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region ActiveToolChanged Event

    [Fact]
    public void WhenActivateTool_ActiveToolChangedEventRaised()
    {
        // Arrange
        ToolChangedEventArgs? received = null;
        _sut.ActiveToolChanged += (_, e) => received = e;

        // Act
        _sut.Activate(ToolIds.Crop);

        // Assert
        received.Should().NotBeNull();
        received!.OldToolId.Should().BeNull();
        received.NewToolId.Should().Be(ToolIds.Crop);
    }

    [Fact]
    public void WhenSwitchTools_EventContainsBothToolIds()
    {
        // Arrange
        _sut.Activate(ToolIds.Crop);
        ToolChangedEventArgs? received = null;
        _sut.ActiveToolChanged += (_, e) => received = e;

        // Act
        _sut.Activate(ToolIds.Drawing);

        // Assert
        received.Should().NotBeNull();
        received!.OldToolId.Should().Be(ToolIds.Crop);
        received.NewToolId.Should().Be(ToolIds.Drawing);
    }

    [Fact]
    public void WhenDeactivateTool_EventNewToolIdIsNull()
    {
        // Arrange
        _sut.Activate(ToolIds.Crop);
        ToolChangedEventArgs? received = null;
        _sut.ActiveToolChanged += (_, e) => received = e;

        // Act
        _sut.Deactivate(ToolIds.Crop);

        // Assert
        received.Should().NotBeNull();
        received!.OldToolId.Should().Be(ToolIds.Crop);
        received.NewToolId.Should().BeNull();
    }

    #endregion

    #region EventBus Integration

    [Fact]
    public void WhenActivateTool_ToolPanelToggledEventPublished()
    {
        // Arrange
        var events = new List<ToolPanelToggledEvent>();
        _services.EventBus.Subscribe<ToolPanelToggledEvent>(e => events.Add(e));

        // Act
        _sut.Activate(ToolIds.Crop);

        // Assert
        events.Should().ContainSingle(e => e.ToolId == ToolIds.Crop && e.IsActive);
    }

    [Fact]
    public void WhenSwitchTools_TwoToggleEventsPublished()
    {
        // Arrange
        _sut.Activate(ToolIds.Crop);
        var events = new List<ToolPanelToggledEvent>();
        _services.EventBus.Subscribe<ToolPanelToggledEvent>(e => events.Add(e));

        // Act
        _sut.Activate(ToolIds.Drawing);

        // Assert — deactivate old + activate new
        events.Should().HaveCount(2);
        events[0].ToolId.Should().Be(ToolIds.Crop);
        events[0].IsActive.Should().BeFalse();
        events[1].ToolId.Should().Be(ToolIds.Drawing);
        events[1].IsActive.Should().BeTrue();
    }

    [Fact]
    public void WhenActivateTool_ToolChangedEventPublishedOnBus()
    {
        // Arrange
        ToolChangedEvent? received = null;
        _services.EventBus.Subscribe<ToolChangedEvent>(e => received = e);

        // Act
        _sut.Activate(ToolIds.Crop);

        // Assert
        received.Should().NotBeNull();
        received!.NewToolId.Should().Be(ToolIds.Crop);
    }

    #endregion
}
