using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using System.Globalization;

namespace DiffusionNexus.UI.Converters;

/// <summary>
/// Multi-value converter for cached thumbnails.
/// This converter is deprecated - use DatasetImageViewModel.Thumbnail property directly instead.
/// </summary>
public class AsyncThumbnailConverter : IMultiValueConverter
{
    /// <summary>
    /// Singleton instance for XAML binding.
    /// </summary>
    public static readonly AsyncThumbnailConverter Instance = new();

    /// <summary>
    /// Thumbnail service instance (set during app initialization).
    /// </summary>
    public static Services.IThumbnailService? ThumbnailService { get; set; }

    /// <summary>
    /// Target width for thumbnails.
    /// </summary>
    public int TargetWidth { get; set; } = 340;

    /// <summary>
    /// Converts path + ViewModel to a bitmap, triggering async load if not cached.
    /// Values[0] = ThumbnailPath (string)
    /// Values[1] = DatasetImageViewModel (for notification callback)
    /// </summary>
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 1)
            return null;

        var path = values[0] as string;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        // Try to get from cache first (synchronous, fast path)
        if (ThumbnailService?.TryGetCached(path, out var cached) == true)
            return cached;

        // Trigger async load (ViewModel handles notification via Thumbnail property)
        _ = ThumbnailService?.LoadThumbnailAsync(path, TargetWidth);

        // Return null while loading - the binding will update when NotifyThumbnailLoaded is called
        return null;
    }
}

/// <summary>
/// Simple synchronous converter for cached thumbnails only.
/// Use when you know the image is already cached.
/// </summary>
public class CachedThumbnailConverter : IValueConverter
{
    public static readonly CachedThumbnailConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrEmpty(path))
            return null;

        if (AsyncThumbnailConverter.ThumbnailService?.TryGetCached(path, out var cached) == true)
            return cached;

        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
