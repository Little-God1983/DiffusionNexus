using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.ImageEditor.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// Unit tests for <see cref="DrawingToolsViewModel"/>.
/// Tests drawing tool activation, color presets, shape type selection, and event raising.
/// </summary>
public class DrawingToolsViewModelTests
{
    private readonly List<string> _deactivatedTools = [];
    private readonly DrawingToolsViewModel _sut;

    public DrawingToolsViewModelTests()
    {
        _sut = new DrawingToolsViewModel(
            hasImage: () => true,
            deactivateOtherTools: tool => _deactivatedTools.Add(tool));
    }

    #region Constructor

    [Fact]
    public void WhenCreated_DefaultStateIsCorrect()
    {
        _sut.IsDrawingToolActive.Should().BeFalse();
        _sut.DrawingBrushRed.Should().Be(255);
        _sut.DrawingBrushGreen.Should().Be(255);
        _sut.DrawingBrushBlue.Should().Be(255);
        _sut.DrawingBrushSize.Should().Be(10f);
        _sut.DrawingBrushShape.Should().Be(BrushShape.Round);
        _sut.SelectedShapeType.Should().Be(ShapeType.Freehand);
        _sut.ShapeFillMode.Should().Be(ShapeFillMode.Stroke);
        _sut.IsShapeFreehand.Should().BeTrue();
        _sut.IsShapeMode.Should().BeFalse();
        _sut.HasPlacedShape.Should().BeFalse();
    }

    [Fact]
    public void WhenCreatedWithNullHasImage_ThrowsArgumentNullException()
    {
        var act = () => new DrawingToolsViewModel(null!, _ => { });
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WhenCreatedWithNullDeactivateOtherTools_ThrowsArgumentNullException()
    {
        var act = () => new DrawingToolsViewModel(() => true, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region Drawing Tool Activation

    [Fact]
    public void WhenDrawingToolActivated_DrawingToolActivatedEventIsRaised()
    {
        bool? received = null;
        _sut.DrawingToolActivated += (_, active) => received = active;

        _sut.IsDrawingToolActive = true;

        received.Should().BeTrue();
    }

    [Fact]
    public void WhenDrawingToolActivated_DeactivateOtherToolsIsCalled()
    {
        _sut.IsDrawingToolActive = true;

        _deactivatedTools.Should().Contain(ToolIds.Drawing);
    }

    [Fact]
    public void WhenDrawingToolActivated_StatusMessageIsSet()
    {
        string? message = null;
        _sut.StatusMessageChanged += (_, msg) => message = msg;

        _sut.IsDrawingToolActive = true;

        message.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void WhenDrawingToolDeactivated_StatusMessageIsCleared()
    {
        _sut.IsDrawingToolActive = true;

        string? message = null;
        _sut.StatusMessageChanged += (_, msg) => message = msg;

        _sut.IsDrawingToolActive = false;

        message.Should().BeNull();
    }

    #endregion

    #region Drawing Brush Color

    [Fact]
    public void WhenBrushRedSet_DrawingBrushColorUpdates()
    {
        _sut.DrawingBrushRed = 128;

        _sut.DrawingBrushColor.R.Should().Be(128);
        _sut.DrawingBrushColorHex.Should().Be("#80FFFF");
    }

    [Fact]
    public void WhenDrawingBrushColorSet_ComponentsUpdate()
    {
        _sut.DrawingBrushColor = Avalonia.Media.Color.FromRgb(10, 20, 30);

        _sut.DrawingBrushRed.Should().Be(10);
        _sut.DrawingBrushGreen.Should().Be(20);
        _sut.DrawingBrushBlue.Should().Be(30);
    }

    [Fact]
    public void WhenBrushColorChanged_DrawingSettingsChangedEventIsRaised()
    {
        DrawingSettings? received = null;
        _sut.DrawingSettingsChanged += (_, settings) => received = settings;

        _sut.DrawingBrushRed = 100;

        received.Should().NotBeNull();
    }

    #endregion

    #region Drawing Brush Size

    [Fact]
    public void WhenBrushSizeSetAboveMax_ValueIsClampedTo100()
    {
        _sut.DrawingBrushSize = 200;
        _sut.DrawingBrushSize.Should().Be(100);
    }

    [Fact]
    public void WhenBrushSizeSetBelowMin_ValueIsClampedTo1()
    {
        _sut.DrawingBrushSize = 0;
        _sut.DrawingBrushSize.Should().Be(1);
    }

    [Fact]
    public void WhenBrushSizeSet_TextIsFormatted()
    {
        _sut.DrawingBrushSize = 42;
        _sut.DrawingBrushSizeText.Should().Be("42 px");
    }

    #endregion

    #region Brush Shape

    [Fact]
    public void WhenSquareBrushSelected_IsSquareBrushIsTrue()
    {
        _sut.IsSquareBrush = true;

        _sut.IsSquareBrush.Should().BeTrue();
        _sut.IsRoundBrush.Should().BeFalse();
        _sut.DrawingBrushShape.Should().Be(BrushShape.Square);
    }

    [Fact]
    public void WhenRoundBrushSelected_IsRoundBrushIsTrue()
    {
        _sut.IsSquareBrush = true;
        _sut.IsRoundBrush = true;

        _sut.IsRoundBrush.Should().BeTrue();
        _sut.IsSquareBrush.Should().BeFalse();
    }

    #endregion

    #region Color Presets

    [Theory]
    [InlineData("White", 255, 255, 255)]
    [InlineData("Black", 0, 0, 0)]
    [InlineData("Red", 255, 0, 0)]
    [InlineData("Green", 0, 255, 0)]
    [InlineData("Blue", 0, 0, 255)]
    [InlineData("Yellow", 255, 255, 0)]
    public void WhenDrawingColorPresetSet_BrushColorUpdates(string preset, byte r, byte g, byte b)
    {
        _sut.SetDrawingColorPreset(preset);

        _sut.DrawingBrushRed.Should().Be(r);
        _sut.DrawingBrushGreen.Should().Be(g);
        _sut.DrawingBrushBlue.Should().Be(b);
    }

    [Fact]
    public void WhenDrawingColorPresetSetToNull_NoChange()
    {
        _sut.DrawingBrushRed = 100;
        _sut.SetDrawingColorPreset(null);
        _sut.DrawingBrushRed.Should().Be(100);
    }

    [Theory]
    [InlineData("White", 255, 255, 255)]
    [InlineData("Red", 255, 0, 0)]
    [InlineData("Cyan", 0, 255, 255)]
    public void WhenShapeFillPresetSet_FillColorUpdates(string preset, byte r, byte g, byte b)
    {
        _sut.SetShapeFillPreset(preset);

        _sut.ShapeFillRed.Should().Be(r);
        _sut.ShapeFillGreen.Should().Be(g);
        _sut.ShapeFillBlue.Should().Be(b);
    }

    [Theory]
    [InlineData("White", 255, 255, 255)]
    [InlineData("Black", 0, 0, 0)]
    [InlineData("Magenta", 255, 0, 255)]
    public void WhenShapeStrokePresetSet_StrokeColorUpdates(string preset, byte r, byte g, byte b)
    {
        _sut.SetShapeStrokePreset(preset);

        _sut.ShapeStrokeRed.Should().Be(r);
        _sut.ShapeStrokeGreen.Should().Be(g);
        _sut.ShapeStrokeBlue.Should().Be(b);
    }

    #endregion

    #region Shape Type Selection

    [Fact]
    public void WhenRectangleSelected_IsShapeModeIsTrue()
    {
        _sut.IsShapeRectangle = true;

        _sut.IsShapeMode.Should().BeTrue();
        _sut.IsShapeFreehand.Should().BeFalse();
        _sut.SelectedShapeType.Should().Be(ShapeType.Rectangle);
    }

    [Fact]
    public void WhenEllipseSelected_IsShapeEllipseIsTrue()
    {
        _sut.IsShapeEllipse = true;

        _sut.IsShapeEllipse.Should().BeTrue();
        _sut.SelectedShapeType.Should().Be(ShapeType.Ellipse);
    }

    [Fact]
    public void WhenArrowSelected_IsShapeArrowIsTrue()
    {
        _sut.IsShapeArrow = true;

        _sut.SelectedShapeType.Should().Be(ShapeType.Arrow);
    }

    [Fact]
    public void WhenLineSelected_IsShapeLineIsTrue()
    {
        _sut.IsShapeLine = true;

        _sut.SelectedShapeType.Should().Be(ShapeType.Line);
    }

    [Fact]
    public void WhenCrossSelected_IsShapeCrossIsTrue()
    {
        _sut.IsShapeCross = true;

        _sut.SelectedShapeType.Should().Be(ShapeType.Cross);
    }

    #endregion

    #region Shape Fill Mode

    [Fact]
    public void WhenFillOnlySelected_IsShapeFillOnlyIsTrue()
    {
        _sut.IsShapeFillOnly = true;

        _sut.IsShapeFillOnly.Should().BeTrue();
        _sut.IsShapeStrokeOnly.Should().BeFalse();
        _sut.ShapeFillMode.Should().Be(ShapeFillMode.Fill);
    }

    [Fact]
    public void WhenFillAndStrokeSelected_IsShapeFillAndStrokeIsTrue()
    {
        _sut.IsShapeFillAndStroke = true;

        _sut.IsShapeFillAndStroke.Should().BeTrue();
        _sut.ShapeFillMode.Should().Be(ShapeFillMode.FillAndStroke);
    }

    #endregion

    #region Shape Colors

    [Fact]
    public void WhenShapeStrokeColorSet_ComponentsUpdate()
    {
        _sut.ShapeStrokeColor = Avalonia.Media.Color.FromRgb(50, 100, 150);

        _sut.ShapeStrokeRed.Should().Be(50);
        _sut.ShapeStrokeGreen.Should().Be(100);
        _sut.ShapeStrokeBlue.Should().Be(150);
        _sut.ShapeStrokeColorHex.Should().Be("#326496");
    }

    [Fact]
    public void WhenShapeFillColorSet_ComponentsUpdate()
    {
        _sut.ShapeFillColor = Avalonia.Media.Color.FromRgb(200, 100, 50);

        _sut.ShapeFillRed.Should().Be(200);
        _sut.ShapeFillGreen.Should().Be(100);
        _sut.ShapeFillBlue.Should().Be(50);
        _sut.ShapeFillColorHex.Should().Be("#C86432");
    }

    [Fact]
    public void WhenShapeStrokeColorChanged_ShapeSettingsChangedEventIsRaised()
    {
        var raised = false;
        _sut.ShapeSettingsChanged += (_, _) => raised = true;

        _sut.ShapeStrokeRed = 42;

        raised.Should().BeTrue();
    }

    #endregion

    #region Shape Stroke Width

    [Fact]
    public void WhenShapeStrokeWidthSetAboveMax_ValueIsClampedTo50()
    {
        _sut.ShapeStrokeWidth = 100;
        _sut.ShapeStrokeWidth.Should().Be(50);
    }

    [Fact]
    public void WhenShapeStrokeWidthSetBelowMin_ValueIsClampedTo1()
    {
        _sut.ShapeStrokeWidth = 0;
        _sut.ShapeStrokeWidth.Should().Be(1);
    }

    [Fact]
    public void WhenShapeStrokeWidthSet_TextIsFormatted()
    {
        _sut.ShapeStrokeWidth = 5;
        _sut.ShapeStrokeWidthText.Should().Be("5 px");
    }

    #endregion

    #region HasPlacedShape

    [Fact]
    public void WhenHasPlacedShapeSetTrue_CommandsNotifyCanExecuteChanged()
    {
        _sut.HasPlacedShape = true;

        _sut.CommitPlacedShapeCommand.CanExecute(null).Should().BeTrue();
        _sut.CancelPlacedShapeCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void WhenHasPlacedShapeFalse_CommitCannotExecute()
    {
        _sut.CommitPlacedShapeCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void WhenCommitPlacedShapeExecuted_EventIsRaised()
    {
        _sut.HasPlacedShape = true;
        var raised = false;
        _sut.CommitPlacedShapeRequested += (_, _) => raised = true;

        _sut.CommitPlacedShapeCommand.Execute(null);

        raised.Should().BeTrue();
    }

    [Fact]
    public void WhenCancelPlacedShapeExecuted_EventIsRaised()
    {
        _sut.HasPlacedShape = true;
        var raised = false;
        _sut.CancelPlacedShapeRequested += (_, _) => raised = true;

        _sut.CancelPlacedShapeCommand.Execute(null);

        raised.Should().BeTrue();
    }

    #endregion

    #region CloseAll

    [Fact]
    public void WhenCloseAll_DrawingToolIsDeactivated()
    {
        _sut.IsDrawingToolActive = true;

        bool? deactivated = null;
        _sut.DrawingToolActivated += (_, active) => deactivated = active;

        _sut.CloseAll();

        _sut.IsDrawingToolActive.Should().BeFalse();
        deactivated.Should().BeFalse();
    }

    #endregion

    #region Toggle Command

    [Fact]
    public void WhenToggleDrawingToolExecuted_DrawingToolIsActivated()
    {
        _sut.ToggleDrawingToolCommand.Execute(null);

        _sut.IsDrawingToolActive.Should().BeTrue();
    }

    [Fact]
    public void WhenToggleDrawingToolExecutedTwice_DrawingToolIsDeactivated()
    {
        _sut.ToggleDrawingToolCommand.Execute(null);
        _sut.ToggleDrawingToolCommand.Execute(null);

        _sut.IsDrawingToolActive.Should().BeFalse();
    }

    [Fact]
    public void WhenHasImageIsFalse_ToggleDrawingToolCannotExecute()
    {
        var sut = new DrawingToolsViewModel(
            hasImage: () => false,
            deactivateOtherTools: _ => { });

        sut.ToggleDrawingToolCommand.CanExecute(null).Should().BeFalse();
    }

    #endregion
}
