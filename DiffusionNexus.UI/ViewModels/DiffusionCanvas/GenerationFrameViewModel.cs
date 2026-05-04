using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DiffusionNexus.UI.ViewModels.DiffusionCanvas;

/// <summary>
/// One generation frame on the infinite canvas. Owns its position, size, prompt, and
/// the lifecycle state of the generation that produced (or is producing) the image inside it.
///
/// The frame is the unit of "history" on the canvas — once a generation completes, the user
/// can drag the frame around freely; pressing Generate again creates a NEW frame next to it
/// rather than overwriting the result.
/// </summary>
public partial class GenerationFrameViewModel : ObservableObject
{
    /// <summary>Model dimension alignment. Z-Image-Turbo requires multiples of 64.</summary>
    public const int DimensionAlignment = 64;

    public const int MinDimension = 512;
    public const int MaxDimension = 2048;

    /// <summary>X position on the canvas, in canvas-space pixels.</summary>
    [ObservableProperty]
    private double _canvasX;

    /// <summary>Y position on the canvas, in canvas-space pixels.</summary>
    [ObservableProperty]
    private double _canvasY;

    /// <summary>Frame width (matches the diffusion output width). Snapped to <see cref="DimensionAlignment"/>.</summary>
    [ObservableProperty]
    private int _width = 1024;

    /// <summary>Frame height (matches the diffusion output height). Snapped to <see cref="DimensionAlignment"/>.</summary>
    [ObservableProperty]
    private int _height = 1024;

    /// <summary>Per-frame prompt text (defaults from the canvas-level prompt at creation time).</summary>
    [ObservableProperty]
    private string _prompt = string.Empty;

    /// <summary>Current lifecycle state. Drives view template selection.</summary>
    [ObservableProperty]
    private GenerationFrameState _state = GenerationFrameState.Idle;

    /// <summary>1-based current sampling step (for <see cref="GenerationFrameState.Sampling"/>).</summary>
    [ObservableProperty]
    private int _stepCurrent;

    /// <summary>Total sampling steps (for <see cref="GenerationFrameState.Sampling"/>).</summary>
    [ObservableProperty]
    private int _stepTotal;

    /// <summary>Human-readable status line shown over the frame ("Loading…", "Sampling 5/9", error message…).</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>The completed image bound to the frame's surface, or null while idle/in-flight.</summary>
    [ObservableProperty]
    private Bitmap? _frameImage;

    /// <summary>Absolute path of the saved PNG, populated once <see cref="State"/> becomes Completed.</summary>
    [ObservableProperty]
    private string? _imagePath;

    /// <summary>Seed actually used for the generation (echoed from the backend), or null while pending.</summary>
    [ObservableProperty]
    private long? _seed;

    /// <summary>True while generation is in flight — disables Generate / drag-to-resize.</summary>
    public bool IsBusy => State is GenerationFrameState.Loading or GenerationFrameState.Sampling;

    partial void OnStateChanged(GenerationFrameState value) => OnPropertyChanged(nameof(IsBusy));

    /// <summary>
    /// Snaps a desired pixel dimension to the model's alignment grid and clamps to the allowed range.
    /// </summary>
    public static int SnapDimension(double desired)
    {
        var snapped = (int)Math.Round(desired / DimensionAlignment) * DimensionAlignment;
        return Math.Clamp(snapped, MinDimension, MaxDimension);
    }

    // TODO(v2-context-menu): wire these commands when the right-click context menu ships:
    //   - SendToImageEditorCommand
    //   - UseAsControlNetReferenceCommand
    //   - UseAsInpaintBaseCommand
    //   - CopySeedToClipboardCommand
    //   - CopyPromptToClipboardCommand
    /// <summary>Right-click → Delete frame. Implemented in v1; bound from the canvas VM.</summary>
    public IRelayCommand<GenerationFrameViewModel>? DeleteCommand { get; set; }
}
