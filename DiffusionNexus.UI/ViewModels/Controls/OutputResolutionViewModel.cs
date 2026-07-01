using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DiffusionNexus.UI.ViewModels.Controls;

/// <summary>Output aspect-ratio choice for <see cref="OutputResolutionViewModel"/>.</summary>
public enum OutputAspectRatio
{
    /// <summary>Match the source image's own aspect ratio.</summary>
    SameAsInput,
    R16x9,
    R1x1,
    R4x3,
    R5x4,
}

/// <summary>
/// Reusable view model for the output-resolution picker: an aspect-ratio choice (the source's own ratio
/// or a common one), an orientation toggle, and a target megapixel budget (0.25–4 MP). It computes the
/// final output dimensions for any source size — width/height solved from the ratio at the megapixel
/// budget, each side a multiple of 16. Pair it with <c>Views.Controls.OutputResolutionControl</c>.
/// </summary>
public partial class OutputResolutionViewModel : ObservableObject
{
    public const double MinMegapixels = 0.25;
    public const double MaxMegapixels = 4.0;

    /// <summary>The chosen aspect ratio (the source's own, or a common one scaled to the target MP).</summary>
    [ObservableProperty] private OutputAspectRatio _selectedAspectRatio = OutputAspectRatio.SameAsInput;

    /// <summary>Flips the chosen ratio's orientation (landscape ↔ portrait). No-op for 1:1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label16x9))]
    [NotifyPropertyChangedFor(nameof(Label4x3))]
    [NotifyPropertyChangedFor(nameof(Label5x4))]
    private bool _switchOrientation;

    /// <summary>Target output size in megapixels (0.25–4). The aspect ratio is preserved at this budget.</summary>
    [ObservableProperty] private double _outputMegapixels = 1.0;

    /// <summary>A live "1360 × 768 (1 MP)" preview of the computed output (for fixed ratios).</summary>
    [ObservableProperty] private string _outputResolutionText = string.Empty;

    /// <summary>Ratio button labels — flip with the orientation toggle (e.g. 16:9 ↔ 9:16).</summary>
    public string Label16x9 => SwitchOrientation ? "9:16" : "16:9";
    public string Label4x3 => SwitchOrientation ? "3:4" : "4:3";
    public string Label5x4 => SwitchOrientation ? "4:5" : "5:4";

    public OutputResolutionViewModel() => UpdateOutputPreview();

    /// <summary>Selects a common output aspect ratio (or "same as input"); bound by the ratio buttons.</summary>
    [RelayCommand]
    private void SelectAspectRatio(OutputAspectRatio ratio) => SelectedAspectRatio = ratio;

    partial void OnSelectedAspectRatioChanged(OutputAspectRatio value) => UpdateOutputPreview();
    partial void OnSwitchOrientationChanged(bool value) => UpdateOutputPreview();
    partial void OnOutputMegapixelsChanged(double value) => UpdateOutputPreview();

    /// <summary>
    /// Computes the output dimensions for a source of the given size: the chosen aspect ratio scaled to
    /// the target megapixel budget, each side a multiple of 16. A non-positive source size falls back to
    /// square for the "same as input" case.
    /// </summary>
    public (int Width, int Height) ComputeDimensions(double sourceWidth, double sourceHeight)
    {
        var aspect = SelectedAspectRatio == OutputAspectRatio.SameAsInput && sourceWidth > 0 && sourceHeight > 0
            ? sourceWidth / sourceHeight
            : FixedAspect();
        return ScaleToMegapixels(OrientedAspect(aspect));
    }

    private double FixedAspect() => SelectedAspectRatio switch
    {
        OutputAspectRatio.R16x9 => 16.0 / 9.0,
        OutputAspectRatio.R1x1 => 1.0,
        OutputAspectRatio.R4x3 => 4.0 / 3.0,
        OutputAspectRatio.R5x4 => 5.0 / 4.0,
        _ => 1.0,
    };

    private double OrientedAspect(double aspect) => SwitchOrientation ? 1.0 / aspect : aspect;

    private (int Width, int Height) ScaleToMegapixels(double aspect)
    {
        var targetPixels = Math.Clamp(OutputMegapixels, MinMegapixels, MaxMegapixels) * 1_000_000.0;
        return (Align(Math.Sqrt(targetPixels * aspect)), Align(Math.Sqrt(targetPixels / aspect)));

        static int Align(double value)
        {
            var v = (int)Math.Round(Math.Clamp(value, 256, 2816));
            v -= v % 16;
            return Math.Max(v, 256);
        }
    }

    private void UpdateOutputPreview()
    {
        if (SelectedAspectRatio == OutputAspectRatio.SameAsInput)
        {
            OutputResolutionText = $"Matches each input image ({OutputMegapixels:0.##} MP)";
        }
        else
        {
            var (w, h) = ScaleToMegapixels(OrientedAspect(FixedAspect()));
            OutputResolutionText = $"{w} × {h} ({OutputMegapixels:0.##} MP)";
        }
    }
}
