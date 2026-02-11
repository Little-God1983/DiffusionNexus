using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.ImageEditor;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Sub-ViewModel managing drawing tool and shape tool state.
/// Extracted from <see cref="ImageEditorViewModel"/> to reduce its size.
/// </summary>
public partial class DrawingToolsViewModel : ObservableObject
{
    private readonly Func<bool> _hasImage;
    private readonly Action<string> _deactivateOtherTools;

    // Drawing tool fields
    private bool _isDrawingToolActive;
    private byte _drawingBrushRed = 255;
    private byte _drawingBrushGreen = 255;
    private byte _drawingBrushBlue = 255;
    private float _drawingBrushSize = 10f;
    private BrushShape _drawingBrushShape = BrushShape.Round;

    // Shape tool fields
    private ShapeType _selectedShapeType = ShapeType.Freehand;
    private ShapeFillMode _shapeFillMode = ShapeFillMode.Stroke;
    private byte _shapeFillRed = 255;
    private byte _shapeFillGreen = 255;
    private byte _shapeFillBlue = 255;
    private byte _shapeStrokeRed = 255;
    private byte _shapeStrokeGreen = 255;
    private byte _shapeStrokeBlue = 255;
    private float _shapeStrokeWidth = 3f;
    private bool _hasPlacedShape;

    public DrawingToolsViewModel(Func<bool> hasImage, Action<string> deactivateOtherTools)
    {
        ArgumentNullException.ThrowIfNull(hasImage);
        ArgumentNullException.ThrowIfNull(deactivateOtherTools);
        _hasImage = hasImage;
        _deactivateOtherTools = deactivateOtherTools;

        ToggleDrawingToolCommand = new RelayCommand(ExecuteToggleDrawingTool, () => _hasImage());
        SetDrawingColorPresetCommand = new RelayCommand<string>(SetDrawingColorPreset);
        SetShapeFillPresetCommand = new RelayCommand<string>(SetShapeFillPreset);
        SetShapeStrokePresetCommand = new RelayCommand<string>(SetShapeStrokePreset);
        CommitPlacedShapeCommand = new RelayCommand(
            () => CommitPlacedShapeRequested?.Invoke(this, EventArgs.Empty),
            () => HasPlacedShape);
        CancelPlacedShapeCommand = new RelayCommand(
            () => CancelPlacedShapeRequested?.Invoke(this, EventArgs.Empty),
            () => HasPlacedShape);
    }

    #region Drawing Tool Properties

    /// <summary>Whether the drawing tool is active.</summary>
    public bool IsDrawingToolActive
    {
        get => _isDrawingToolActive;
        set
        {
            if (SetProperty(ref _isDrawingToolActive, value))
            {
                if (value)
                    _deactivateOtherTools(nameof(IsDrawingToolActive));
                DrawingToolActivated?.Invoke(this, value);
                ToolStateChanged?.Invoke(this, EventArgs.Empty);
                StatusMessageChanged?.Invoke(this, value ? "Draw: Click and drag to draw. Hold Shift for straight lines." : null);
            }
        }
    }

    /// <summary>Red component of the brush color (0-255).</summary>
    public byte DrawingBrushRed
    {
        get => _drawingBrushRed;
        set
        {
            if (SetProperty(ref _drawingBrushRed, value))
            {
                OnPropertyChanged(nameof(DrawingBrushColor));
                OnPropertyChanged(nameof(DrawingBrushColorHex));
                DrawingSettingsChanged?.Invoke(this, CurrentDrawingSettings);
            }
        }
    }

    /// <summary>Green component of the brush color (0-255).</summary>
    public byte DrawingBrushGreen
    {
        get => _drawingBrushGreen;
        set
        {
            if (SetProperty(ref _drawingBrushGreen, value))
            {
                OnPropertyChanged(nameof(DrawingBrushColor));
                OnPropertyChanged(nameof(DrawingBrushColorHex));
                DrawingSettingsChanged?.Invoke(this, CurrentDrawingSettings);
            }
        }
    }

    /// <summary>Blue component of the brush color (0-255).</summary>
    public byte DrawingBrushBlue
    {
        get => _drawingBrushBlue;
        set
        {
            if (SetProperty(ref _drawingBrushBlue, value))
            {
                OnPropertyChanged(nameof(DrawingBrushColor));
                OnPropertyChanged(nameof(DrawingBrushColorHex));
                DrawingSettingsChanged?.Invoke(this, CurrentDrawingSettings);
            }
        }
    }

    /// <summary>The current brush color as an Avalonia Color.</summary>
    public Avalonia.Media.Color DrawingBrushColor
    {
        get => Avalonia.Media.Color.FromRgb(_drawingBrushRed, _drawingBrushGreen, _drawingBrushBlue);
        set
        {
            if (_drawingBrushRed != value.R || _drawingBrushGreen != value.G || _drawingBrushBlue != value.B)
            {
                _drawingBrushRed = value.R;
                _drawingBrushGreen = value.G;
                _drawingBrushBlue = value.B;
                OnPropertyChanged(nameof(DrawingBrushRed));
                OnPropertyChanged(nameof(DrawingBrushGreen));
                OnPropertyChanged(nameof(DrawingBrushBlue));
                OnPropertyChanged(nameof(DrawingBrushColor));
                OnPropertyChanged(nameof(DrawingBrushColorHex));
                DrawingSettingsChanged?.Invoke(this, CurrentDrawingSettings);
            }
        }
    }

    /// <summary>Hex string representation of the brush color.</summary>
    public string DrawingBrushColorHex => $"#{_drawingBrushRed:X2}{_drawingBrushGreen:X2}{_drawingBrushBlue:X2}";

    /// <summary>Brush size in pixels (1-100).</summary>
    public float DrawingBrushSize
    {
        get => _drawingBrushSize;
        set
        {
            var clamped = Math.Clamp(value, 1f, 100f);
            if (SetProperty(ref _drawingBrushSize, clamped))
            {
                OnPropertyChanged(nameof(DrawingBrushSizeText));
                DrawingSettingsChanged?.Invoke(this, CurrentDrawingSettings);
            }
        }
    }

    /// <summary>Formatted brush size for display.</summary>
    public string DrawingBrushSizeText => $"{(int)_drawingBrushSize} px";

    /// <summary>The current brush shape.</summary>
    public BrushShape DrawingBrushShape
    {
        get => _drawingBrushShape;
        set
        {
            if (SetProperty(ref _drawingBrushShape, value))
            {
                OnPropertyChanged(nameof(IsRoundBrush));
                OnPropertyChanged(nameof(IsSquareBrush));
                DrawingSettingsChanged?.Invoke(this, CurrentDrawingSettings);
            }
        }
    }

    /// <summary>Whether the round brush shape is selected.</summary>
    public bool IsRoundBrush
    {
        get => _drawingBrushShape == BrushShape.Round;
        set { if (value) DrawingBrushShape = BrushShape.Round; }
    }

    /// <summary>Whether the square brush shape is selected.</summary>
    public bool IsSquareBrush
    {
        get => _drawingBrushShape == BrushShape.Square;
        set { if (value) DrawingBrushShape = BrushShape.Square; }
    }

    /// <summary>Gets the current drawing settings.</summary>
    public DrawingSettings CurrentDrawingSettings => new()
    {
        Color = SkiaSharp.SKColor.FromHsl(0, 0, 0).WithRed(_drawingBrushRed).WithGreen(_drawingBrushGreen).WithBlue(_drawingBrushBlue),
        Size = _drawingBrushSize,
        Shape = _drawingBrushShape
    };

    #endregion

    #region Shape Tool Properties

    /// <summary>The currently selected shape type.</summary>
    public ShapeType SelectedShapeType
    {
        get => _selectedShapeType;
        set
        {
            if (SetProperty(ref _selectedShapeType, value))
            {
                OnPropertyChanged(nameof(IsShapeFreehand));
                OnPropertyChanged(nameof(IsShapeRectangle));
                OnPropertyChanged(nameof(IsShapeEllipse));
                OnPropertyChanged(nameof(IsShapeArrow));
                OnPropertyChanged(nameof(IsShapeLine));
                OnPropertyChanged(nameof(IsShapeCross));
                OnPropertyChanged(nameof(IsShapeMode));
                ShapeSettingsChanged?.Invoke(this, EventArgs.Empty);
                UpdateDrawingModeStatus();
            }
        }
    }

    /// <summary>Whether freehand drawing is selected.</summary>
    public bool IsShapeFreehand
    {
        get => _selectedShapeType == ShapeType.Freehand;
        set { if (value) SelectedShapeType = ShapeType.Freehand; }
    }

    /// <summary>Whether rectangle shape is selected.</summary>
    public bool IsShapeRectangle
    {
        get => _selectedShapeType == ShapeType.Rectangle;
        set { if (value) SelectedShapeType = ShapeType.Rectangle; }
    }

    /// <summary>Whether ellipse shape is selected.</summary>
    public bool IsShapeEllipse
    {
        get => _selectedShapeType == ShapeType.Ellipse;
        set { if (value) SelectedShapeType = ShapeType.Ellipse; }
    }

    /// <summary>Whether arrow shape is selected.</summary>
    public bool IsShapeArrow
    {
        get => _selectedShapeType == ShapeType.Arrow;
        set { if (value) SelectedShapeType = ShapeType.Arrow; }
    }

    /// <summary>Whether line shape is selected.</summary>
    public bool IsShapeLine
    {
        get => _selectedShapeType == ShapeType.Line;
        set { if (value) SelectedShapeType = ShapeType.Line; }
    }

    /// <summary>Whether cross/X shape is selected.</summary>
    public bool IsShapeCross
    {
        get => _selectedShapeType == ShapeType.Cross;
        set { if (value) SelectedShapeType = ShapeType.Cross; }
    }

    /// <summary>Whether a shape mode (not freehand) is selected.</summary>
    public bool IsShapeMode => _selectedShapeType != ShapeType.Freehand;

    /// <summary>The shape fill mode.</summary>
    public ShapeFillMode ShapeFillMode
    {
        get => _shapeFillMode;
        set
        {
            if (SetProperty(ref _shapeFillMode, value))
            {
                OnPropertyChanged(nameof(IsShapeStrokeOnly));
                OnPropertyChanged(nameof(IsShapeFillOnly));
                OnPropertyChanged(nameof(IsShapeFillAndStroke));
                ShapeSettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Whether stroke only mode is selected.</summary>
    public bool IsShapeStrokeOnly
    {
        get => _shapeFillMode == ShapeFillMode.Stroke;
        set { if (value) ShapeFillMode = ShapeFillMode.Stroke; }
    }

    /// <summary>Whether fill only mode is selected.</summary>
    public bool IsShapeFillOnly
    {
        get => _shapeFillMode == ShapeFillMode.Fill;
        set { if (value) ShapeFillMode = ShapeFillMode.Fill; }
    }

    /// <summary>Whether fill and stroke mode is selected.</summary>
    public bool IsShapeFillAndStroke
    {
        get => _shapeFillMode == ShapeFillMode.FillAndStroke;
        set { if (value) ShapeFillMode = ShapeFillMode.FillAndStroke; }
    }

    /// <summary>Red component of the shape fill color (0-255).</summary>
    public byte ShapeFillRed
    {
        get => _shapeFillRed;
        set { if (SetProperty(ref _shapeFillRed, value)) OnShapeFillColorChanged(); }
    }

    /// <summary>Green component of the shape fill color (0-255).</summary>
    public byte ShapeFillGreen
    {
        get => _shapeFillGreen;
        set { if (SetProperty(ref _shapeFillGreen, value)) OnShapeFillColorChanged(); }
    }

    /// <summary>Blue component of the shape fill color (0-255).</summary>
    public byte ShapeFillBlue
    {
        get => _shapeFillBlue;
        set { if (SetProperty(ref _shapeFillBlue, value)) OnShapeFillColorChanged(); }
    }

    /// <summary>The shape fill color as an Avalonia Color.</summary>
    public Avalonia.Media.Color ShapeFillColor
    {
        get => Avalonia.Media.Color.FromRgb(_shapeFillRed, _shapeFillGreen, _shapeFillBlue);
        set
        {
            if (_shapeFillRed != value.R || _shapeFillGreen != value.G || _shapeFillBlue != value.B)
            {
                _shapeFillRed = value.R; _shapeFillGreen = value.G; _shapeFillBlue = value.B;
                OnPropertyChanged(nameof(ShapeFillRed)); OnPropertyChanged(nameof(ShapeFillGreen)); OnPropertyChanged(nameof(ShapeFillBlue));
                OnShapeFillColorChanged();
            }
        }
    }

    /// <summary>Hex string representation of the fill color.</summary>
    public string ShapeFillColorHex => $"#{_shapeFillRed:X2}{_shapeFillGreen:X2}{_shapeFillBlue:X2}";

    /// <summary>Red component of the shape stroke color (0-255).</summary>
    public byte ShapeStrokeRed
    {
        get => _shapeStrokeRed;
        set { if (SetProperty(ref _shapeStrokeRed, value)) OnShapeStrokeColorChanged(); }
    }

    /// <summary>Green component of the shape stroke color (0-255).</summary>
    public byte ShapeStrokeGreen
    {
        get => _shapeStrokeGreen;
        set { if (SetProperty(ref _shapeStrokeGreen, value)) OnShapeStrokeColorChanged(); }
    }

    /// <summary>Blue component of the shape stroke color (0-255).</summary>
    public byte ShapeStrokeBlue
    {
        get => _shapeStrokeBlue;
        set { if (SetProperty(ref _shapeStrokeBlue, value)) OnShapeStrokeColorChanged(); }
    }

    /// <summary>The shape stroke color as an Avalonia Color.</summary>
    public Avalonia.Media.Color ShapeStrokeColor
    {
        get => Avalonia.Media.Color.FromRgb(_shapeStrokeRed, _shapeStrokeGreen, _shapeStrokeBlue);
        set
        {
            if (_shapeStrokeRed != value.R || _shapeStrokeGreen != value.G || _shapeStrokeBlue != value.B)
            {
                _shapeStrokeRed = value.R; _shapeStrokeGreen = value.G; _shapeStrokeBlue = value.B;
                OnPropertyChanged(nameof(ShapeStrokeRed)); OnPropertyChanged(nameof(ShapeStrokeGreen)); OnPropertyChanged(nameof(ShapeStrokeBlue));
                OnShapeStrokeColorChanged();
            }
        }
    }

    /// <summary>Hex string representation of the stroke color.</summary>
    public string ShapeStrokeColorHex => $"#{_shapeStrokeRed:X2}{_shapeStrokeGreen:X2}{_shapeStrokeBlue:X2}";

    /// <summary>Shape stroke width in pixels (1-50).</summary>
    public float ShapeStrokeWidth
    {
        get => _shapeStrokeWidth;
        set
        {
            var clamped = Math.Clamp(value, 1f, 50f);
            if (SetProperty(ref _shapeStrokeWidth, clamped))
            {
                OnPropertyChanged(nameof(ShapeStrokeWidthText));
                ShapeSettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Formatted stroke width for display.</summary>
    public string ShapeStrokeWidthText => $"{(int)_shapeStrokeWidth} px";

    /// <summary>Whether a shape is currently placed and awaiting commit/cancel.</summary>
    public bool HasPlacedShape
    {
        get => _hasPlacedShape;
        set
        {
            if (SetProperty(ref _hasPlacedShape, value))
            {
                CommitPlacedShapeCommand.NotifyCanExecuteChanged();
                CancelPlacedShapeCommand.NotifyCanExecuteChanged();
            }
        }
    }

    #endregion

    #region Commands

    public IRelayCommand ToggleDrawingToolCommand { get; }
    public IRelayCommand<string> SetDrawingColorPresetCommand { get; }
    public IRelayCommand<string> SetShapeFillPresetCommand { get; }
    public IRelayCommand<string> SetShapeStrokePresetCommand { get; }
    public RelayCommand CommitPlacedShapeCommand { get; }
    public RelayCommand CancelPlacedShapeCommand { get; }

    #endregion

    #region Events

    /// <summary>Raised when drawing tool is activated or deactivated.</summary>
    public event EventHandler<bool>? DrawingToolActivated;

    /// <summary>Raised when drawing settings (color, size, shape) change.</summary>
    public event EventHandler<DrawingSettings>? DrawingSettingsChanged;

    /// <summary>Raised when shape settings change.</summary>
    public event EventHandler? ShapeSettingsChanged;

    /// <summary>Raised when tool state changes (for parent ViewModel notification).</summary>
    public event EventHandler? ToolStateChanged;

    /// <summary>Raised when a status message should be displayed.</summary>
    public event EventHandler<string?>? StatusMessageChanged;

    /// <summary>Raised when a tool is toggled via the ToolManager.</summary>
    public event EventHandler<(string ToolId, bool IsActive)>? ToolToggled;

    /// <summary>Raised when the ViewModel requests committing the placed shape.</summary>
    public event EventHandler? CommitPlacedShapeRequested;

    /// <summary>Raised when the ViewModel requests cancelling the placed shape.</summary>
    public event EventHandler? CancelPlacedShapeRequested;

    #endregion

    #region Public Methods

    /// <summary>
    /// Notifies all commands that their CanExecute state may have changed.
    /// </summary>
    public void RefreshCommandStates()
    {
        ToggleDrawingToolCommand.NotifyCanExecuteChanged();
        CommitPlacedShapeCommand.NotifyCanExecuteChanged();
        CancelPlacedShapeCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Closes the drawing tool. Called by the parent when clearing/resetting.
    /// </summary>
    public void CloseAll()
    {
        if (_isDrawingToolActive)
        {
            _isDrawingToolActive = false;
            OnPropertyChanged(nameof(IsDrawingToolActive));
            DrawingToolActivated?.Invoke(this, false);
        }
    }

    /// <summary>Sets the brush color from a preset.</summary>
    public void SetDrawingColorPreset(string? preset)
    {
        if (preset is null) return;

        (byte r, byte g, byte b) = preset.ToUpperInvariant() switch
        {
            "WHITE" => ((byte)255, (byte)255, (byte)255),
            "BLACK" => ((byte)0, (byte)0, (byte)0),
            "RED" => ((byte)255, (byte)0, (byte)0),
            "GREEN" => ((byte)0, (byte)255, (byte)0),
            "BLUE" => ((byte)0, (byte)0, (byte)255),
            "YELLOW" => ((byte)255, (byte)255, (byte)0),
            _ => (_drawingBrushRed, _drawingBrushGreen, _drawingBrushBlue)
        };

        DrawingBrushRed = r; DrawingBrushGreen = g; DrawingBrushBlue = b;
    }

    /// <summary>Sets the shape fill color from a preset.</summary>
    public void SetShapeFillPreset(string? preset)
    {
        if (preset is null) return;

        (byte r, byte g, byte b) = preset.ToUpperInvariant() switch
        {
            "WHITE" => ((byte)255, (byte)255, (byte)255),
            "BLACK" => ((byte)0, (byte)0, (byte)0),
            "RED" => ((byte)255, (byte)0, (byte)0),
            "GREEN" => ((byte)0, (byte)255, (byte)0),
            "BLUE" => ((byte)0, (byte)0, (byte)255),
            "YELLOW" => ((byte)255, (byte)255, (byte)0),
            "ORANGE" => ((byte)255, (byte)165, (byte)0),
            "PURPLE" => ((byte)128, (byte)0, (byte)128),
            "CYAN" => ((byte)0, (byte)255, (byte)255),
            "MAGENTA" => ((byte)255, (byte)0, (byte)255),
            "GRAY" or "GREY" => ((byte)128, (byte)128, (byte)128),
            "TRANSPARENT" or "NONE" => ((byte)0, (byte)0, (byte)0),
            _ => (_shapeFillRed, _shapeFillGreen, _shapeFillBlue)
        };

        ShapeFillRed = r; ShapeFillGreen = g; ShapeFillBlue = b;
    }

    /// <summary>Sets the shape stroke color from a preset.</summary>
    public void SetShapeStrokePreset(string? preset)
    {
        if (preset is null) return;

        (byte r, byte g, byte b) = preset.ToUpperInvariant() switch
        {
            "WHITE" => ((byte)255, (byte)255, (byte)255),
            "BLACK" => ((byte)0, (byte)0, (byte)0),
            "RED" => ((byte)255, (byte)0, (byte)0),
            "GREEN" => ((byte)0, (byte)255, (byte)0),
            "BLUE" => ((byte)0, (byte)0, (byte)255),
            "YELLOW" => ((byte)255, (byte)255, (byte)0),
            "ORANGE" => ((byte)255, (byte)165, (byte)0),
            "PURPLE" => ((byte)128, (byte)0, (byte)128),
            "CYAN" => ((byte)0, (byte)255, (byte)255),
            "MAGENTA" => ((byte)255, (byte)0, (byte)255),
            "GRAY" or "GREY" => ((byte)128, (byte)128, (byte)128),
            _ => (_shapeStrokeRed, _shapeStrokeGreen, _shapeStrokeBlue)
        };

        ShapeStrokeRed = r; ShapeStrokeGreen = g; ShapeStrokeBlue = b;
    }

    #endregion

    #region Private Methods

    private void OnShapeFillColorChanged()
    {
        OnPropertyChanged(nameof(ShapeFillColor));
        OnPropertyChanged(nameof(ShapeFillColorHex));
        ShapeSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnShapeStrokeColorChanged()
    {
        OnPropertyChanged(nameof(ShapeStrokeColor));
        OnPropertyChanged(nameof(ShapeStrokeColorHex));
        ShapeSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ExecuteToggleDrawingTool()
    {
        IsDrawingToolActive = !IsDrawingToolActive;
        ToolToggled?.Invoke(this, (ImageEditor.Services.ToolIds.Drawing, IsDrawingToolActive));
    }

    private void UpdateDrawingModeStatus()
    {
        if (!IsDrawingToolActive) return;

        StatusMessageChanged?.Invoke(this, _selectedShapeType switch
        {
            ShapeType.Freehand => "Draw: Click and drag to draw. Hold Shift for straight lines.",
            ShapeType.Rectangle => "Rectangle: Click and drag to draw a rectangle.",
            ShapeType.Ellipse => "Ellipse: Click and drag to draw an ellipse.",
            ShapeType.Arrow => "Arrow: Click and drag to draw an arrow.",
            ShapeType.Line => "Line: Click and drag to draw a straight line.",
            _ => null
        });
    }

    #endregion
}
