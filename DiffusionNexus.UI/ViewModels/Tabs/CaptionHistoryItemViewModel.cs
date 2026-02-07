using Avalonia.Media.Imaging;

namespace DiffusionNexus.UI.ViewModels.Tabs;

/// <summary>
/// Represents a single completed caption result in the processing history.
/// </summary>
public sealed class CaptionHistoryItemViewModel : ViewModelBase, IDisposable
{
    private const int PreviewLength = 160;

    private bool _isExpanded;
    private bool _disposed;

    /// <summary>
    /// Creates a new history item from a completed captioning result.
    /// </summary>
    public CaptionHistoryItemViewModel(string imagePath, string caption, Bitmap? thumbnail)
    {
        ImagePath = imagePath;
        FileName = Path.GetFileName(imagePath);
        FullCaption = caption;
        CaptionPreview = caption.Length > PreviewLength
            ? string.Concat(caption.AsSpan(0, PreviewLength), "...")
            : caption;
        HasMoreText = caption.Length > PreviewLength;
        Thumbnail = thumbnail;
    }

    /// <summary>
    /// Full path to the source image.
    /// </summary>
    public string ImagePath { get; }

    /// <summary>
    /// Display file name.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// Full generated caption text.
    /// </summary>
    public string FullCaption { get; }

    /// <summary>
    /// Truncated caption for the list view.
    /// </summary>
    public string CaptionPreview { get; }

    /// <summary>
    /// Whether the full caption is longer than the preview.
    /// </summary>
    public bool HasMoreText { get; }

    /// <summary>
    /// Thumbnail bitmap for the list view.
    /// </summary>
    public Bitmap? Thumbnail { get; }

    /// <summary>
    /// Whether the full caption is shown inline.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>
    /// The caption text to display based on expanded state.
    /// </summary>
    public string DisplayCaption => IsExpanded ? FullCaption : CaptionPreview;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        Thumbnail?.Dispose();
        _disposed = true;
    }
}
