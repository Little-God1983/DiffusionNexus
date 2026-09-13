using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.ImageEditor.Services;
using Serilog;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Panel state for the Move / Transform tool. Owns nothing pixel-related: it raises requests
/// (position, size, rotation, flips, reset, apply) that the view forwards to
/// <see cref="LayerTransformTool"/>, and it is told the tool's state through
/// <see cref="UpdateFromTool"/> / <see cref="OnIneligible"/> / <see cref="OnApplied"/> /
/// <see cref="OnApplyFailed"/>.
/// </summary>
public partial class LayerTransformViewModel : ObservableObject
{
    private static readonly ILogger Logger = Log.ForContext<LayerTransformViewModel>();
    private const string LogSource = "LayerTransform";

    public const string IneligibleHintText = "This layer can't be moved. Select another layer.";
    public const string TooLargeHintText = "The transformed layer would be too large.";
    public const string AllocationHintText = "Could not allocate the transformed layer.";
    public const string StatusOnOpen = "Move: Drag the layer to move it, use the handles to scale or rotate. Enter applies, Escape resets.";

    private readonly Func<bool> _hasImage;
    private readonly Action<string> _deactivateOtherTools;
    private readonly IUnifiedLogger? _unifiedLogger;

    private bool _isPanelOpen;
    private string _layerName = string.Empty;
    private int _x, _y, _width, _height;
    private float _rotationDegrees;
    private bool _keepAspect = true;
    private bool _hasTransform;
    private string _hintText = string.Empty;
    private bool _isHintVisible;
    private bool _syncing;

    public LayerTransformViewModel(Func<bool> hasImage, Action<string> deactivateOtherTools, IUnifiedLogger? unifiedLogger = null)
    {
        ArgumentNullException.ThrowIfNull(hasImage);
        ArgumentNullException.ThrowIfNull(deactivateOtherTools);
        _hasImage = hasImage;
        _deactivateOtherTools = deactivateOtherTools;
        _unifiedLogger = unifiedLogger;

        ToggleCommand = new RelayCommand(() => IsPanelOpen = !IsPanelOpen, () => _hasImage());
        CancelCommand = new RelayCommand(() =>
        {
            // Discard before closing: closing deactivates the tool, and deactivating with a
            // pending transform commits it (Shape/Text precedent). Reset first makes that
            // deactivate-commit a no-op.
            EmitInfo("cancelled (transform discarded)");
            ResetRequested?.Invoke(this, EventArgs.Empty);
            IsPanelOpen = false;
        }, () => IsPanelOpen);
        ResetCommand = new RelayCommand(() => { EmitInfo("reset requested"); ResetRequested?.Invoke(this, EventArgs.Empty); }, () => IsPanelOpen);
        ApplyCommand = new RelayCommand(() => { EmitInfo("apply requested"); ApplyRequested?.Invoke(this, EventArgs.Empty); }, () => _hasImage() && IsPanelOpen && HasTransform);
        FlipHorizontalCommand = new RelayCommand(() => { EmitInfo("flip horizontal"); FlipRequested?.Invoke(this, true); }, () => IsPanelOpen);
        FlipVerticalCommand = new RelayCommand(() => { EmitInfo("flip vertical"); FlipRequested?.Invoke(this, false); }, () => IsPanelOpen);
    }

    #region Properties

    public bool IsPanelOpen
    {
        get => _isPanelOpen;
        set
        {
            if (!SetProperty(ref _isPanelOpen, value)) return;
            IsHintVisible = false;
            if (value)
            {
                _deactivateOtherTools(ToolIds.LayerTransform);
                EmitInfo("panel opened");
                ToolActivated?.Invoke(this, EventArgs.Empty);
                StatusMessageChanged?.Invoke(this, StatusOnOpen);
            }
            else
            {
                EmitInfo("panel closed");
                ToolDeactivated?.Invoke(this, EventArgs.Empty);
                StatusMessageChanged?.Invoke(this, null);
            }
            RefreshCommandStates();
            ToolToggled?.Invoke(this, (ToolIds.LayerTransform, value));
            ToolStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string LayerName { get => _layerName; private set => SetProperty(ref _layerName, value); }

    /// <summary>Left of the transformed box, canvas px.</summary>
    public int X
    {
        get => _x;
        set { if (SetProperty(ref _x, value) && !_syncing) PositionRequested?.Invoke(this, (value, _y)); }
    }

    public int Y
    {
        get => _y;
        set { if (SetProperty(ref _y, value) && !_syncing) PositionRequested?.Invoke(this, (_x, value)); }
    }

    public int Width
    {
        get => _width;
        set { if (SetProperty(ref _width, value) && !_syncing && value > 0) SizeRequested?.Invoke(this, (value, _height)); }
    }

    public int Height
    {
        get => _height;
        set { if (SetProperty(ref _height, value) && !_syncing && value > 0) SizeRequested?.Invoke(this, (_width, value)); }
    }

    public float RotationDegrees
    {
        get => _rotationDegrees;
        set { if (SetProperty(ref _rotationDegrees, value) && !_syncing) RotationRequested?.Invoke(this, value); }
    }

    public bool KeepAspect
    {
        get => _keepAspect;
        set { if (SetProperty(ref _keepAspect, value)) KeepAspectChanged?.Invoke(this, value); }
    }

    public bool HasTransform
    {
        get => _hasTransform;
        private set { if (SetProperty(ref _hasTransform, value)) ApplyCommand.NotifyCanExecuteChanged(); }
    }

    public string HintText { get => _hintText; private set => SetProperty(ref _hintText, value); }
    public bool IsHintVisible { get => _isHintVisible; private set => SetProperty(ref _isHintVisible, value); }

    #endregion

    #region Commands

    public IRelayCommand ToggleCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand ResetCommand { get; }
    public IRelayCommand ApplyCommand { get; }
    public IRelayCommand FlipHorizontalCommand { get; }
    public IRelayCommand FlipVerticalCommand { get; }

    #endregion

    #region Events

    public event EventHandler? ToolActivated;
    public event EventHandler? ToolDeactivated;
    public event EventHandler<(string ToolId, bool IsActive)>? ToolToggled;
    public event EventHandler? ToolStateChanged;
    public event EventHandler<string?>? StatusMessageChanged;
    public event EventHandler<(float X, float Y)>? PositionRequested;
    public event EventHandler<(float W, float H)>? SizeRequested;
    public event EventHandler<float>? RotationRequested;
    public event EventHandler<bool>? KeepAspectChanged;
    /// <summary>true = horizontal, false = vertical.</summary>
    public event EventHandler<bool>? FlipRequested;
    public event EventHandler? ResetRequested;
    public event EventHandler? ApplyRequested;

    #endregion

    #region Public methods

    public void RefreshCommandStates()
    {
        ToggleCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        FlipHorizontalCommand.NotifyCanExecuteChanged();
        FlipVerticalCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Closes the panel without deactivating other tools (tool coordination).</summary>
    public void ClosePanel()
    {
        if (!_isPanelOpen) return;
        _isPanelOpen = false;
        OnPropertyChanged(nameof(IsPanelOpen));
        IsHintVisible = false;
        ToolDeactivated?.Invoke(this, EventArgs.Empty);
        RefreshCommandStates();
    }

    /// <summary>Called by the view on every tool change. Syncs fields without echoing requests.</summary>
    public void UpdateFromTool(string layerName, float x, float y, float w, float h, float rotation, bool hasTransform)
    {
        _syncing = true;
        try
        {
            LayerName = layerName;
            X = (int)MathF.Round(x);
            Y = (int)MathF.Round(y);
            Width = (int)MathF.Round(w);
            Height = (int)MathF.Round(h);
            RotationDegrees = MathF.Round(rotation, 1);
            HasTransform = hasTransform;
        }
        finally
        {
            _syncing = false;
        }
    }

    public void OnIneligible(LayerTransformEligibility eligibility)
    {
        if (eligibility == LayerTransformEligibility.Ok) { IsHintVisible = false; return; }
        EmitInfo($"active layer not eligible: {eligibility}");
        HintText = IneligibleHintText;
        IsHintVisible = true;
        HasTransform = false;
    }

    public void OnApplied()
    {
        EmitInfo("applied");
        IsHintVisible = false;
        StatusMessageChanged?.Invoke(this, "Layer transformed.");
    }

    public void OnApplyFailed(LayerTransformFailure failure)
    {
        EmitInfo($"apply failed: {failure}");
        HintText = failure == LayerTransformFailure.TooLarge ? TooLargeHintText : AllocationHintText;
        IsHintVisible = true;
    }

    #endregion

    private void EmitInfo(string message)
    {
        Logger.Information("LayerTransform: {Message}", message);
        _unifiedLogger?.Info(LogCategory.Configuration, LogSource, message); // same category CanvasExtendViewModel uses
    }
}
