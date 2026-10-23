using System.Globalization;
using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.DiffusionCanvas;

namespace DiffusionNexus.UI.ViewModels.DiffusionCanvas;

/// <summary>
/// The canvas's inpaint mask (#595, #518 region D2): where the next image-to-image run may repaint.
/// </summary>
/// <remarks>
/// <para>
/// The mask is a list of world-space strokes, not pixels. The canvas is unbounded and zoomable, so a
/// bitmap would need a size and a resolution it cannot know in advance; strokes cost almost nothing,
/// stay sharp at every zoom and rasterise to exactly the box's region at Generate
/// (<see cref="CanvasMaskRasterizer"/>).
/// </para>
/// <para>
/// <see cref="Strokes"/> is replaced, never mutated, so a snapshot taken for the render thread or a
/// background rasterise cannot change under it.
/// </para>
/// </remarks>
public sealed partial class InpaintMaskLayerViewModel : ObservableObject, ILayerStackItem, ICanvasMask
{
    /// <summary>Smallest feather, in world pixels: hard edges.</summary>
    public const double MinFeather = 0;

    /// <summary>Largest feather, in world pixels.</summary>
    public const double MaxFeather = 64;

    /// <summary>Lowest denoise a masked run can be given. Zero would keep the masked area unchanged.</summary>
    public const double MinDenoise = 0.05;

    /// <summary>Highest denoise.</summary>
    public const double MaxDenoise = 1.0;

    /// <summary>
    /// Default denoise for a masked run: higher than the panel's image-to-image default, because a
    /// masked area is usually meant to change rather than be touched up.
    /// </summary>
    public const double DefaultDenoise = 0.75;

    private readonly Action<string> _trace;
    private IReadOnlyList<CanvasMaskStroke> _strokes = [];

    public InpaintMaskLayerViewModel(Action<string>? trace = null)
    {
        _trace = trace ?? (_ => { });
    }

    /// <summary>What this layer holds.</summary>
    public CanvasLayerKind Kind => CanvasLayerKind.InpaintMask;

    // ────────────────────────────── ILayerStackItem ──────────────────────────────

    /// <summary>Layer name shown in the layer stack.</summary>
    [ObservableProperty]
    private string _name = "Inpaint mask";

    /// <summary>Whether the mask is drawn and sent. A hidden mask takes no part in a run.</summary>
    [ObservableProperty]
    private bool _isVisible = true;

    /// <summary>Protects the mask from Delete and Clear canvas.</summary>
    [ObservableProperty]
    private bool _isLocked;

    /// <summary>A mask has no opacity: it is an instruction to the model, not pixels in the picture.</summary>
    public string OpacityText => string.Empty;

    /// <summary>No thumbnail: the strokes only mean something on the canvas.</summary>
    public Bitmap? Thumbnail => null;

    // ────────────────────────────── Strokes ──────────────────────────────

    /// <summary>The strokes in the order they were drawn.</summary>
    public IReadOnlyList<CanvasMaskStroke> Strokes => _strokes;

    /// <summary>True when anything has been painted or erased.</summary>
    public bool HasStrokes => _strokes.Count > 0;

    /// <summary>
    /// Bumps on every change to <see cref="Strokes"/>. The surface redraws on it, and the canvas
    /// re-reads whether the mask meets the box.
    /// </summary>
    [ObservableProperty]
    private int _revision;

    /// <summary>
    /// Everything the brush strokes can have painted, or null when nothing was. Erasers only remove, so
    /// they do not grow it; the area can therefore be larger than what is left after erasing, and
    /// Generate measures the real share from the rasterised mask.
    /// </summary>
    public Rect? PaintedBounds { get; private set; }

    /// <summary>Appends a finished stroke.</summary>
    public void AddStroke(CanvasMaskStroke stroke)
    {
        ArgumentNullException.ThrowIfNull(stroke);

        _strokes = [.. _strokes, stroke];
        if (!stroke.IsErase)
            PaintedBounds = PaintedBounds is { } bounds ? bounds.Union(stroke.Bounds) : stroke.Bounds;

        _trace(string.Create(
            CultureInfo.InvariantCulture,
            $"{(stroke.IsErase ? "Erased" : "Painted")} a {stroke.Size:0} px stroke on '{Name}' ({_strokes.Count} stroke(s)) at ({stroke.Bounds.X:0}, {stroke.Bounds.Y:0}, {stroke.Bounds.Width:0}×{stroke.Bounds.Height:0})."));
        StrokesChanged();
    }

    /// <summary>Removes every stroke.</summary>
    [RelayCommand(CanExecute = nameof(HasStrokes))]
    public void ClearStrokes()
    {
        if (_strokes.Count == 0)
            return;

        var count = _strokes.Count;
        _strokes = [];
        PaintedBounds = null;
        _trace($"Cleared the mask '{Name}' ({count} stroke(s) removed).");
        StrokesChanged();
    }

    private void StrokesChanged()
    {
        OnPropertyChanged(nameof(Strokes));
        OnPropertyChanged(nameof(HasStrokes));
        OnPropertyChanged(nameof(PaintedBounds));
        ClearStrokesCommand.NotifyCanExecuteChanged();
        Revision++;
    }

    // ────────────────────────────── Inspector ──────────────────────────────

    private double _feather;

    /// <summary>Feather in world pixels, <see cref="MinFeather"/> to <see cref="MaxFeather"/>.</summary>
    public double Feather
    {
        get => _feather;
        set
        {
            var clamped = double.IsNaN(value) ? MinFeather : Math.Clamp(Math.Round(value), MinFeather, MaxFeather);
            if (SetProperty(ref _feather, clamped))
                OnPropertyChanged(nameof(FeatherText));
        }
    }

    /// <summary>Feather as display text, formatted invariantly.</summary>
    public string FeatherText => string.Create(CultureInfo.InvariantCulture, $"{Feather:0} px");

    /// <summary>Repaint everything inside the box except what is painted.</summary>
    [ObservableProperty]
    private bool _invert;

    partial void OnInvertChanged(bool value) =>
        _trace(value
            ? $"Mask '{Name}' inverted: everything in the box except the painting will be repainted."
            : $"Mask '{Name}' no longer inverted: only the painting will be repainted.");

    private double _denoise = DefaultDenoise;

    /// <summary>
    /// Denoise for a run the mask takes part in, <see cref="MinDenoise"/> to <see cref="MaxDenoise"/>. It
    /// replaces the panel's Denoise for that run.
    /// </summary>
    public double Denoise
    {
        get => _denoise;
        set
        {
            var clamped = double.IsNaN(value) ? DefaultDenoise : Math.Clamp(Math.Round(value, 2), MinDenoise, MaxDenoise);
            if (SetProperty(ref _denoise, clamped))
                OnPropertyChanged(nameof(DenoiseText));
        }
    }

    /// <summary>Denoise as display text, formatted invariantly.</summary>
    public string DenoiseText => string.Create(CultureInfo.InvariantCulture, $"{Denoise:0.00}");
}
