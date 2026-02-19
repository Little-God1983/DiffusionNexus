using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.ImageEditor;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Sub-ViewModel managing text tool state.
/// Supports font selection, text color, outline color/width, and text element lifecycle.
/// </summary>
public partial class TextToolViewModel : ObservableObject
{
    private readonly Func<bool> _hasImage;
    private readonly Action<string> _deactivateOtherTools;

    private bool _isTextToolActive;
    private string _text = "Text";
    private string _fontFamily = "Arial";
    private float _fontSize = 48f;
    private bool _isBold;
    private bool _isItalic;
    private byte _textColorRed = 255;
    private byte _textColorGreen = 255;
    private byte _textColorBlue = 255;
    private byte _outlineColorRed;
    private byte _outlineColorGreen;
    private byte _outlineColorBlue;
    private float _outlineWidth;
    private bool _hasPlacedText;

    public TextToolViewModel(Func<bool> hasImage, Action<string> deactivateOtherTools)
    {
        ArgumentNullException.ThrowIfNull(hasImage);
        ArgumentNullException.ThrowIfNull(deactivateOtherTools);
        _hasImage = hasImage;
        _deactivateOtherTools = deactivateOtherTools;

        ToggleTextToolCommand = new RelayCommand(ExecuteToggleTextTool, () => _hasImage());
        PlaceTextCommand = new RelayCommand(
            () => PlaceTextRequested?.Invoke(this, EventArgs.Empty),
            () => _isTextToolActive && _hasImage());
        CommitPlacedTextCommand = new RelayCommand(
            () => CommitPlacedTextRequested?.Invoke(this, EventArgs.Empty),
            () => HasPlacedText);
        CancelPlacedTextCommand = new RelayCommand(
            () => CancelPlacedTextRequested?.Invoke(this, EventArgs.Empty),
            () => HasPlacedText);
    }

    #region Text Tool Properties

    /// <summary>Whether the text tool is active.</summary>
    public bool IsTextToolActive
    {
        get => _isTextToolActive;
        set
        {
            if (SetProperty(ref _isTextToolActive, value))
            {
                if (value)
                    _deactivateOtherTools(ImageEditor.Services.ToolIds.Text);
                TextToolActivated?.Invoke(this, value);
                ToolStateChanged?.Invoke(this, EventArgs.Empty);
                StatusMessageChanged?.Invoke(this, value ? "Text: Click on the image to place text." : null);
                PlaceTextCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>The text content to place.</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
                TextSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The font family name.</summary>
    public string FontFamily
    {
        get => _fontFamily;
        set
        {
            if (SetProperty(ref _fontFamily, value))
                TextSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Font size in pixels (8-500).</summary>
    public float FontSize
    {
        get => _fontSize;
        set
        {
            var clamped = Math.Clamp(value, 8f, 500f);
            if (SetProperty(ref _fontSize, clamped))
            {
                OnPropertyChanged(nameof(FontSizeText));
                TextSettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Formatted font size for display.</summary>
    public string FontSizeText => $"{(int)_fontSize} px";

    /// <summary>Whether the font is bold.</summary>
    public bool IsBold
    {
        get => _isBold;
        set
        {
            if (SetProperty(ref _isBold, value))
                TextSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Whether the font is italic.</summary>
    public bool IsItalic
    {
        get => _isItalic;
        set
        {
            if (SetProperty(ref _isItalic, value))
                TextSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Red component of the text color (0-255).</summary>
    public byte TextColorRed
    {
        get => _textColorRed;
        set { if (SetProperty(ref _textColorRed, value)) OnTextColorChanged(); }
    }

    /// <summary>Green component of the text color (0-255).</summary>
    public byte TextColorGreen
    {
        get => _textColorGreen;
        set { if (SetProperty(ref _textColorGreen, value)) OnTextColorChanged(); }
    }

    /// <summary>Blue component of the text color (0-255).</summary>
    public byte TextColorBlue
    {
        get => _textColorBlue;
        set { if (SetProperty(ref _textColorBlue, value)) OnTextColorChanged(); }
    }

    /// <summary>The text color as an Avalonia Color.</summary>
    public Avalonia.Media.Color TextColor
    {
        get => Avalonia.Media.Color.FromRgb(_textColorRed, _textColorGreen, _textColorBlue);
        set
        {
            if (_textColorRed != value.R || _textColorGreen != value.G || _textColorBlue != value.B)
            {
                _textColorRed = value.R; _textColorGreen = value.G; _textColorBlue = value.B;
                OnPropertyChanged(nameof(TextColorRed)); OnPropertyChanged(nameof(TextColorGreen)); OnPropertyChanged(nameof(TextColorBlue));
                OnTextColorChanged();
            }
        }
    }

    /// <summary>Hex string representation of the text color.</summary>
    public string TextColorHex => $"#{_textColorRed:X2}{_textColorGreen:X2}{_textColorBlue:X2}";

    /// <summary>Red component of the outline color (0-255).</summary>
    public byte OutlineColorRed
    {
        get => _outlineColorRed;
        set { if (SetProperty(ref _outlineColorRed, value)) OnOutlineColorChanged(); }
    }

    /// <summary>Green component of the outline color (0-255).</summary>
    public byte OutlineColorGreen
    {
        get => _outlineColorGreen;
        set { if (SetProperty(ref _outlineColorGreen, value)) OnOutlineColorChanged(); }
    }

    /// <summary>Blue component of the outline color (0-255).</summary>
    public byte OutlineColorBlue
    {
        get => _outlineColorBlue;
        set { if (SetProperty(ref _outlineColorBlue, value)) OnOutlineColorChanged(); }
    }

    /// <summary>The outline color as an Avalonia Color.</summary>
    public Avalonia.Media.Color OutlineColor
    {
        get => Avalonia.Media.Color.FromRgb(_outlineColorRed, _outlineColorGreen, _outlineColorBlue);
        set
        {
            if (_outlineColorRed != value.R || _outlineColorGreen != value.G || _outlineColorBlue != value.B)
            {
                _outlineColorRed = value.R; _outlineColorGreen = value.G; _outlineColorBlue = value.B;
                OnPropertyChanged(nameof(OutlineColorRed)); OnPropertyChanged(nameof(OutlineColorGreen)); OnPropertyChanged(nameof(OutlineColorBlue));
                OnOutlineColorChanged();
            }
        }
    }

    /// <summary>Hex string representation of the outline color.</summary>
    public string OutlineColorHex => $"#{_outlineColorRed:X2}{_outlineColorGreen:X2}{_outlineColorBlue:X2}";

    /// <summary>Outline width in pixels (0-20).</summary>
    public float OutlineWidth
    {
        get => _outlineWidth;
        set
        {
            var clamped = Math.Clamp(value, 0f, 20f);
            if (SetProperty(ref _outlineWidth, clamped))
            {
                OnPropertyChanged(nameof(OutlineWidthText));
                TextSettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Formatted outline width for display.</summary>
    public string OutlineWidthText => $"{(int)_outlineWidth} px";

    /// <summary>Whether a text element is currently placed and awaiting commit/cancel.</summary>
    public bool HasPlacedText
    {
        get => _hasPlacedText;
        set
        {
            if (SetProperty(ref _hasPlacedText, value))
            {
                CommitPlacedTextCommand.NotifyCanExecuteChanged();
                CancelPlacedTextCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Available font families for selection.</summary>
    public string[] AvailableFonts { get; } =
    [
        "Arial",
        "Courier New",
        "Georgia",
        "Impact",
        "Segoe UI",
        "Tahoma",
        "Times New Roman",
        "Trebuchet MS",
        "Verdana",
        // TODO: Linux Implementation - add Linux-specific fonts
    ];

    #endregion

    #region Commands

    public IRelayCommand ToggleTextToolCommand { get; }
    public RelayCommand PlaceTextCommand { get; }
    public RelayCommand CommitPlacedTextCommand { get; }
    public RelayCommand CancelPlacedTextCommand { get; }

    #endregion

    #region Events

    /// <summary>Raised when text tool is activated or deactivated.</summary>
    public event EventHandler<bool>? TextToolActivated;

    /// <summary>Raised when text settings change.</summary>
    public event EventHandler? TextSettingsChanged;

    /// <summary>Raised when tool state changes (for parent ViewModel notification).</summary>
    public event EventHandler? ToolStateChanged;

    /// <summary>Raised when a status message should be displayed.</summary>
    public event EventHandler<string?>? StatusMessageChanged;

    /// <summary>Raised when a tool is toggled via the ToolManager.</summary>
    public event EventHandler<(string ToolId, bool IsActive)>? ToolToggled;

    /// <summary>Raised when the ViewModel requests placing a new text element.</summary>
    public event EventHandler? PlaceTextRequested;

    /// <summary>Raised when the ViewModel requests committing the placed text element.</summary>
    public event EventHandler? CommitPlacedTextRequested;

    /// <summary>Raised when the ViewModel requests cancelling the placed text element.</summary>
    public event EventHandler? CancelPlacedTextRequested;

    #endregion

    #region Public Methods

    /// <summary>
    /// Notifies all commands that their CanExecute state may have changed.
    /// </summary>
    public void RefreshCommandStates()
    {
        ToggleTextToolCommand.NotifyCanExecuteChanged();
        PlaceTextCommand.NotifyCanExecuteChanged();
        CommitPlacedTextCommand.NotifyCanExecuteChanged();
        CancelPlacedTextCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Closes the text tool. Called by the parent when clearing/resetting.
    /// </summary>
    public void CloseAll()
    {
        if (_isTextToolActive)
        {
            _isTextToolActive = false;
            OnPropertyChanged(nameof(IsTextToolActive));
            TextToolActivated?.Invoke(this, false);
        }
    }

    #endregion

    #region Private Methods

    private void OnTextColorChanged()
    {
        OnPropertyChanged(nameof(TextColor));
        OnPropertyChanged(nameof(TextColorHex));
        TextSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnOutlineColorChanged()
    {
        OnPropertyChanged(nameof(OutlineColor));
        OnPropertyChanged(nameof(OutlineColorHex));
        TextSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ExecuteToggleTextTool()
    {
        IsTextToolActive = !IsTextToolActive;
        ToolToggled?.Invoke(this, (ImageEditor.Services.ToolIds.Text, IsTextToolActive));
    }

    #endregion
}
