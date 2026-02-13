using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.UI.Converters;
using DiffusionNexus.UI.Services;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Represents an image entry that can be assigned to either side of the comparer.
/// Loads a thumbnail asynchronously on first access via the <see cref="IThumbnailOrchestrator"/>.
/// </summary>
public partial class ImageCompareItem : ObservableObject
{
    private static readonly ThumbnailOwnerToken _ownerToken = new("ImageComparer");
    private Bitmap? _thumbnail;
    private bool _isThumbnailLoading;

    public ImageCompareItem(string imagePath, string displayName)
    {
        ImagePath = imagePath;
        DisplayName = displayName;
    }

    public string ImagePath { get; }
    public string DisplayName { get; }

    [ObservableProperty]
    private bool _isSelectedLeft;

    [ObservableProperty]
    private bool _isSelectedRight;

    /// <summary>
    /// The loaded thumbnail bitmap. Loads asynchronously on first access.
    /// </summary>
    public Bitmap? Thumbnail
    {
        get
        {
            if (_thumbnail is not null)
                return _thumbnail;

            var orchestrator = PathToBitmapConverter.ThumbnailOrchestrator;
            if (orchestrator?.TryGetCached(ImagePath, out var cached) == true && cached is not null)
            {
                _thumbnail = cached;
                return _thumbnail;
            }

            if (!_isThumbnailLoading)
            {
                _isThumbnailLoading = true;
                _ = LoadThumbnailAsync();
            }

            return null;
        }
    }

    private async Task LoadThumbnailAsync()
    {
        var orchestrator = PathToBitmapConverter.ThumbnailOrchestrator;
        if (orchestrator is null)
        {
            _isThumbnailLoading = false;
            return;
        }

        try
        {
            var bitmap = await orchestrator.RequestThumbnailAsync(
                ImagePath, _ownerToken, ThumbnailPriority.Normal).ConfigureAwait(false);

            if (bitmap is not null)
            {
                if (Dispatcher.UIThread.CheckAccess())
                {
                    _thumbnail = bitmap;
                    _isThumbnailLoading = false;
                    OnPropertyChanged(nameof(Thumbnail));
                }
                else
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        _thumbnail = bitmap;
                        _isThumbnailLoading = false;
                        OnPropertyChanged(nameof(Thumbnail));
                    });
                }
            }
            else
            {
                _isThumbnailLoading = false;
            }
        }
        catch
        {
            _isThumbnailLoading = false;
        }
    }
}
