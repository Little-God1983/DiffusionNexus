using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.UI.DiffusionCanvas;

namespace DiffusionNexus.UI.ViewModels.DiffusionCanvas;

/// <summary>
/// One accepted result on the canvas: pixels at a world position and size.
///
/// This used to be the canvas's whole spatial model — Generate appended a frame at a walking diagonal
/// offset and the user dragged it around afterwards. Issue #518 replaced that with a single
/// <see cref="GenerationBoundingBox"/> that declares where the next generation lands, so a frame is now
/// simply a committed raster: the record of a candidate the user accepted. It no longer moves or resizes,
/// because moving a result after the fact would desynchronise it from the pixels it was generated from.
/// </summary>
public partial class GenerationFrameViewModel : ObservableObject, ICanvasRaster, IDisposable
{
    /// <summary>X position on the canvas, in world units.</summary>
    [ObservableProperty]
    private double _canvasX;

    /// <summary>Y position on the canvas, in world units.</summary>
    [ObservableProperty]
    private double _canvasY;

    /// <summary>Raster width (matches the diffusion output width).</summary>
    [ObservableProperty]
    private int _width = 1024;

    /// <summary>Raster height (matches the diffusion output height).</summary>
    [ObservableProperty]
    private int _height = 1024;

    /// <summary>The prompt that produced this result, kept for provenance.</summary>
    [ObservableProperty]
    private string _prompt = string.Empty;

    /// <summary>Current lifecycle state. Accepted results are always <see cref="GenerationFrameState.Completed"/>.</summary>
    [ObservableProperty]
    private GenerationFrameState _state = GenerationFrameState.Idle;

    /// <summary>1-based current sampling step (for <see cref="GenerationFrameState.Sampling"/>).</summary>
    [ObservableProperty]
    private int _stepCurrent;

    /// <summary>Total sampling steps (for <see cref="GenerationFrameState.Sampling"/>).</summary>
    [ObservableProperty]
    private int _stepTotal;

    /// <summary>Human-readable status line ("Done in 4.1s", an error message…).</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>The image the surface draws. The frame owns it once a candidate is accepted.</summary>
    [ObservableProperty]
    private Bitmap? _frameImage;

    /// <summary>
    /// Absolute path of the saved PNG. <see cref="CanvasRegionCompositor"/> reads the region back from
    /// here, so a frame without a path contributes nothing to an img2img generation under it.
    /// </summary>
    [ObservableProperty]
    private string? _imagePath;

    /// <summary>Seed actually used for the generation (echoed from the backend), or null when unknown.</summary>
    [ObservableProperty]
    private long? _seed;

    /// <summary>The raster's world rectangle — the one definition every intersection test uses.</summary>
    public Rect WorldRect => new(CanvasX, CanvasY, Width, Height);

    /// <summary>True while generation is in flight.</summary>
    public bool IsBusy => State is GenerationFrameState.Loading or GenerationFrameState.Sampling;

    partial void OnStateChanged(GenerationFrameState value) => OnPropertyChanged(nameof(IsBusy));

    // TODO(v2-context-menu): the surface's right-click flyout offers Delete only today (bound to the
    // canvas view model's DeleteFrameCommand). Add these there when they ship:
    //   - SendToImageEditorCommand
    //   - UseAsControlNetReferenceCommand
    //   - CopySeedToClipboardCommand
    //   - CopyPromptToClipboardCommand

    /// <summary>
    /// Releases the raster's bitmap. Callers must detach the frame from the bound collection first —
    /// disposing a bitmap still bound into the visual tree faults the render.
    /// </summary>
    public void Dispose()
    {
        var image = FrameImage;
        FrameImage = null;

        try
        {
            image?.Dispose();
        }
        catch (Exception)
        {
            // Same rule as the staged candidate: releasing a bitmap must never be able to take down a
            // teardown path. A bitmap we cannot release is a leak, not a crash.
        }

        GC.SuppressFinalize(this);
    }
}
