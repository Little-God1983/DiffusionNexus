using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.ImageEditor.Services;
using Serilog;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Panel state for the Canvas Extend tool. Owns nothing pixel-related: it raises requests
/// (target size, aspect preset, apply) that the view forwards to
/// <see cref="ImageEditor.CanvasExtendTool"/>, and it is told the result through
/// <see cref="UpdateResolution"/> / <see cref="OnShrinkAttempted"/> / <see cref="OnApplied"/>.
/// </summary>
public partial class CanvasExtendViewModel : ObservableObject
{
    private static readonly ILogger Logger = Log.ForContext<CanvasExtendViewModel>();
    private const string LogSource = "CanvasExtend";

    /// <summary>Shown when the user tries to make the canvas smaller than the image.</summary>
    public const string ShrinkHintText = "The canvas can only grow here. To cut the image down, use the Crop tool.";

    private readonly Func<bool> _hasImage;
    private readonly Func<int> _getImageWidth;
    private readonly Func<int> _getImageHeight;
    private readonly Action<string> _deactivateOtherTools;
    private readonly IUnifiedLogger? _unifiedLogger;

    private bool _isPanelOpen;
    private string _resolutionText = string.Empty;
    private string _originalSizeText = string.Empty;
    private bool _hasExtension;
    private int _targetWidth;
    private int _targetHeight;
    private bool _isShrinkHintVisible;
    private CanvasAnchor? _selectedAnchor = CanvasAnchor.TopLeft;
    private bool _syncing;
    private long _lastReportedArea;

    public CanvasExtendViewModel(
        Func<bool> hasImage,
        Func<int> getImageWidth,
        Func<int> getImageHeight,
        Action<string> deactivateOtherTools,
        IUnifiedLogger? unifiedLogger = null)
    {
        ArgumentNullException.ThrowIfNull(hasImage);
        ArgumentNullException.ThrowIfNull(getImageWidth);
        ArgumentNullException.ThrowIfNull(getImageHeight);
        ArgumentNullException.ThrowIfNull(deactivateOtherTools);

        _hasImage = hasImage;
        _getImageWidth = getImageWidth;
        _getImageHeight = getImageHeight;
        _deactivateOtherTools = deactivateOtherTools;
        _unifiedLogger = unifiedLogger;

        ToggleCommand = new RelayCommand(() => IsPanelOpen = !IsPanelOpen, () => _hasImage());
        CancelCommand = new RelayCommand(() => IsPanelOpen = false, () => IsPanelOpen);
        ApplyCommand = new RelayCommand(ExecuteApply, () => _hasImage() && IsPanelOpen && HasExtension);
        MultiplyCommand = new RelayCommand<string>(ExecuteMultiply, _ => _hasImage() && IsPanelOpen);
        SetAspectRatioCommand = new RelayCommand<string>(ExecuteSetAspectRatio, _ => _hasImage() && IsPanelOpen);
        SetAnchorCommand = new RelayCommand<CanvasAnchor>(ExecuteSetAnchor, _ => _hasImage() && IsPanelOpen);
        OpenCropCommand = new RelayCommand(() => OpenCropRequested?.Invoke(this, EventArgs.Empty), () => IsPanelOpen);
    }

    #region Properties

    /// <summary>Whether the Extend panel (and tool) is open.</summary>
    public bool IsPanelOpen
    {
        get => _isPanelOpen;
        set
        {
            if (!SetProperty(ref _isPanelOpen, value)) return;

            IsShrinkHintVisible = false;
            if (value)
            {
                _deactivateOtherTools(ToolIds.CanvasExtend);
                EmitInfo($"panel opened for {_getImageWidth()}x{_getImageHeight()}");
                ToolActivated?.Invoke(this, EventArgs.Empty);
                StatusMessageChanged?.Invoke(this, "Extend: Drag a handle outward or type a size. Press Enter to apply, Escape to reset.");
            }
            else
            {
                EmitInfo("panel closed");
                ToolDeactivated?.Invoke(this, EventArgs.Empty);
                StatusMessageChanged?.Invoke(this, null);
            }

            RefreshCommandStates();
            ToolToggled?.Invoke(this, (ToolIds.CanvasExtend, value));
            ToolStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Resolution text for the extended canvas (e.g. "2048 x 1024").</summary>
    public string ResolutionText
    {
        get => _resolutionText;
        private set => SetProperty(ref _resolutionText, value);
    }

    /// <summary>"from W x H" line under the resolution.</summary>
    public string OriginalSizeText
    {
        get => _originalSizeText;
        private set => SetProperty(ref _originalSizeText, value);
    }

    /// <summary>True once the tool reports any extension; gates Apply.</summary>
    public bool HasExtension
    {
        get => _hasExtension;
        private set
        {
            if (SetProperty(ref _hasExtension, value))
                ApplyCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Target canvas width in pixels (two-way bound to the W field).</summary>
    public int TargetWidth
    {
        get => _targetWidth;
        set => SetTarget(ref _targetWidth, value, _getImageWidth(), nameof(TargetWidth));
    }

    /// <summary>Target canvas height in pixels (two-way bound to the H field).</summary>
    public int TargetHeight
    {
        get => _targetHeight;
        set => SetTarget(ref _targetHeight, value, _getImageHeight(), nameof(TargetHeight));
    }

    /// <summary>Whether the "canvas can only grow, use Crop" hint is showing.</summary>
    public bool IsShrinkHintVisible
    {
        get => _isShrinkHintVisible;
        private set => SetProperty(ref _isShrinkHintVisible, value);
    }

    /// <summary>
    /// The placement cell highlighted in the panel's 3x3 grid, or null once the user has
    /// dragged the image to a spot of their own. Set through <see cref="SetAnchorCommand"/>
    /// and reported back by the tool through <see cref="UpdateAnchor"/>.
    /// </summary>
    public CanvasAnchor? SelectedAnchor
    {
        get => _selectedAnchor;
        private set => SetProperty(ref _selectedAnchor, value);
    }

    #endregion

    #region Commands

    public IRelayCommand ToggleCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand ApplyCommand { get; }
    /// <summary>Parameter: "kxW" or "kxH" with k = 1, 2 or 3; "1x" returns that axis to the image size.</summary>
    public IRelayCommand<string> MultiplyCommand { get; }
    /// <summary>Parameter: "W:H", e.g. "16:9".</summary>
    public IRelayCommand<string> SetAspectRatioCommand { get; }
    /// <summary>Parameter: one of the nine <see cref="CanvasAnchor"/> positions (not Custom).</summary>
    public IRelayCommand<CanvasAnchor> SetAnchorCommand { get; }
    public IRelayCommand OpenCropCommand { get; }

    #endregion

    #region Events

    public event EventHandler? ToolActivated;
    public event EventHandler? ToolDeactivated;
    public event EventHandler<(string ToolId, bool IsActive)>? ToolToggled;
    public event EventHandler? ToolStateChanged;
    public event EventHandler<string?>? StatusMessageChanged;
    public event EventHandler? ApplyRequested;
    public event EventHandler<(int Width, int Height)>? TargetSizeRequested;
    public event EventHandler<(float W, float H)>? SetAspectRatioRequested;
    public event EventHandler<CanvasAnchor>? AnchorRequested;
    public event EventHandler? OpenCropRequested;

    #endregion

    #region Public Methods

    /// <summary>Notifies all commands that their CanExecute state may have changed.</summary>
    public void RefreshCommandStates()
    {
        ToggleCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        MultiplyCommand.NotifyCanExecuteChanged();
        SetAspectRatioCommand.NotifyCanExecuteChanged();
        SetAnchorCommand.NotifyCanExecuteChanged();
        OpenCropCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Closes the panel without deactivating other tools (called by tool coordination).</summary>
    public void ClosePanel()
    {
        if (!_isPanelOpen) return;

        _isPanelOpen = false;
        OnPropertyChanged(nameof(IsPanelOpen));
        IsShrinkHintVisible = false;
        ToolDeactivated?.Invoke(this, EventArgs.Empty);
        RefreshCommandStates();
    }

    /// <summary>
    /// Called by the view whenever the tool's region changes (and once on activation).
    /// Syncs the text, the Apply gate and the size fields without echoing a request back.
    /// </summary>
    public void UpdateResolution(int newWidth, int newHeight, bool hasExtension)
    {
        ResolutionText = newWidth > 0 && newHeight > 0 ? $"{newWidth} x {newHeight}" : string.Empty;
        OriginalSizeText = $"from {_getImageWidth()} x {_getImageHeight()}";
        HasExtension = hasExtension;

        var area = (long)Math.Max(0, newWidth) * Math.Max(0, newHeight);
        if (_lastReportedArea > 0 && area > _lastReportedArea)
            IsShrinkHintVisible = false;
        _lastReportedArea = area;

        _syncing = true;
        try
        {
            TargetWidth = newWidth;
            TargetHeight = newHeight;
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// Called by the view whenever the tool reports its placement (with every region change).
    /// A dragged image reports <see cref="CanvasAnchor.Custom"/>, which clears the grid.
    /// </summary>
    public void UpdateAnchor(CanvasAnchor anchor)
    {
        SelectedAnchor = anchor == CanvasAnchor.Custom ? null : anchor;
    }

    /// <summary>Called by the view when the tool blocked an inward drag.</summary>
    public void OnShrinkAttempted()
    {
        EmitInfo("shrink attempt blocked (handle pulled inside the image)");
        IsShrinkHintVisible = true;
    }

    /// <summary>Called by the view after the core applied the extension.</summary>
    public void OnApplied(int newWidth, int newHeight)
    {
        EmitInfo($"applied -> {newWidth}x{newHeight}");
        // Close first: closing resets the tool, which reports a fresh region and would
        // re-populate exactly the state cleared below.
        IsPanelOpen = false;
        ResolutionText = string.Empty;
        HasExtension = false;
        _lastReportedArea = 0;
        StatusMessageChanged?.Invoke(this, $"Canvas extended to {newWidth} x {newHeight}");
    }

    /// <summary>
    /// Called by the view when the core could not apply the extension (allocation failure).
    /// The panel stays open with the extension intact so the user can pick a smaller size;
    /// the view owns the status message.
    /// </summary>
    public void OnApplyFailed()
    {
        EmitInfo("apply failed");
    }

    #endregion

    #region Command Implementations

    private void ExecuteApply()
    {
        EmitInfo($"apply requested ({ResolutionText})");
        ApplyRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExecuteMultiply(string? factor)
    {
        if (string.IsNullOrWhiteSpace(factor) || factor.Length != 3) return;
        if (!int.TryParse(factor[..1], out var k) || k < 1) return;

        var width = _targetWidth > 0 ? _targetWidth : _getImageWidth();
        var height = _targetHeight > 0 ? _targetHeight : _getImageHeight();

        switch (char.ToUpperInvariant(factor[2]))
        {
            case 'W': width = k * _getImageWidth(); break;
            case 'H': height = k * _getImageHeight(); break;
            default: return;
        }

        EmitInfo($"multiplier {factor} -> target {width}x{height}");
        TargetSizeRequested?.Invoke(this, (width, height));
    }

    private void ExecuteSetAspectRatio(string? ratio)
    {
        if (string.IsNullOrWhiteSpace(ratio)) return;

        var parts = ratio.Split(':');
        if (parts.Length != 2 ||
            !float.TryParse(parts[0], out var w) ||
            !float.TryParse(parts[1], out var h))
            return;

        EmitInfo($"aspect preset {ratio}");
        SetAspectRatioRequested?.Invoke(this, (w, h));
    }

    private void ExecuteSetAnchor(CanvasAnchor anchor)
    {
        if (anchor == CanvasAnchor.Custom) return;

        EmitInfo($"image placement {anchor}");
        SelectedAnchor = anchor;
        AnchorRequested?.Invoke(this, anchor);
    }

    #endregion

    private void SetTarget(ref int field, int value, int imageDimension, string propertyName)
    {
        if (_syncing)
        {
            SetProperty(ref field, value, propertyName);
            return;
        }

        if (!_isPanelOpen)
        {
            // The fields exist while the panel is closed (bindings stay alive). Store the
            // value, but do not drive a tool that is not active or show a hint nobody asked for.
            SetProperty(ref field, value, propertyName);
            return;
        }

        var minimum = Math.Max(1, imageDimension);
        if (value < minimum)
        {
            EmitInfo($"typed {propertyName} {value} is below the image ({minimum}); clamped");
            field = minimum;
            OnPropertyChanged(propertyName);
            IsShrinkHintVisible = true;
            return;
        }

        if (!SetProperty(ref field, value, propertyName)) return;

        EmitInfo($"target size {_targetWidth}x{_targetHeight}");
        TargetSizeRequested?.Invoke(this, (_targetWidth, _targetHeight));
    }

    private void EmitInfo(string message)
    {
        Logger.Information("CanvasExtend: {Message}", message);
        _unifiedLogger?.Info(LogCategory.Configuration, LogSource, message);
    }
}
