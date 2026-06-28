using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffusionNexus.UI.ViewModels.Controls;

/// <summary>Processing state of one image in a batch — drives the colour of its status tile.</summary>
public enum ImageProcessingStatus
{
    Pending,
    Processing,
    Done,
    Failed,
}

/// <summary>
/// Reusable item for <see cref="Views.Controls.ImageStatusStrip"/>: one image in a batch carrying a
/// thumbnail, a before(input)/after(output) file-path pair for a comparison view, and a live
/// <see cref="ImageProcessingStatus"/>. Mirrors the Batch Upscale queue item but with a real status
/// enum (incl. a Failed/red state) so it can be reused by any batch feature.
/// </summary>
public partial class ImageStatusItemViewModel : ObservableObject
{
    /// <summary>Display file name shown under the thumbnail.</summary>
    [ObservableProperty] private string _fileName = string.Empty;

    /// <summary>Full path to the input (BEFORE) image — feeds the comparison control's left side.</summary>
    [ObservableProperty] private string _inputPath = string.Empty;

    /// <summary>Full path to the produced (AFTER) image — feeds the right side. Null until <see cref="ImageProcessingStatus.Done"/>.</summary>
    [ObservableProperty] private string? _outputPath;

    /// <summary>Small thumbnail of the input image shown in the tile.</summary>
    [ObservableProperty] private Bitmap? _thumbnail;

    /// <summary>Live processing status (the tile outline colour).</summary>
    [ObservableProperty] private ImageProcessingStatus _status = ImageProcessingStatus.Pending;
}
