using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using DiffusionNexus.UI.Services;
using System.Globalization;

namespace DiffusionNexus.UI.Converters;

/// <summary>
/// Converts a file path string to a Bitmap for display in Image controls.
/// Uses async loading with caching via ThumbnailService when available.
/// Falls back to synchronous DecodeToWidth if ThumbnailService is not configured.
/// 
/// ConverterParameter options:
/// - "full" or "fullres": Load full resolution image (bypasses ThumbnailService)
/// - null or "thumbnail": Use ThumbnailService for 340px thumbnails
/// </summary>
public class PathToBitmapConverter : IValueConverter
{
    /// <summary>
    /// Singleton instance for use in XAML.
    /// </summary>
    public static readonly PathToBitmapConverter Instance = new();

    /// <summary>
    /// Thumbnail service for async loading with caching. Set during app initialization.
    /// </summary>
    [Obsolete("Use ThumbnailOrchestrator for priority-based loading. This property is kept for legacy fallback only.")]
    public static IThumbnailService? ThumbnailService { get; set; }

    /// <summary>
    /// Thumbnail orchestrator for priority-based loading across views. Set during app initialization.
    /// </summary>
    public static IThumbnailOrchestrator? ThumbnailOrchestrator { get; set; }

    /// <summary>
    /// Default width to decode thumbnails to. Matches the card width.
    /// </summary>
    public static int DefaultThumbnailWidth { get; set; } = 340;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        // Check if full resolution is requested via ConverterParameter
        var paramStr = parameter as string;
        var isFullRes = string.Equals(paramStr, "full", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(paramStr, "fullres", StringComparison.OrdinalIgnoreCase);

        if (isFullRes)
        {
            // Load full resolution image synchronously (for image viewer, editor, etc.)
            return LoadFullResolution(path);
        }

        // If ThumbnailService is available, use it for cached async loading
        if (ThumbnailService is not null)
        {
            // Check cache first (synchronous, fast path)
            if (ThumbnailService.TryGetCached(path, out var cached))
                return cached;

            // Trigger async load - the binding will be refreshed when complete
            _ = LoadThumbnailAsync(path);
            
            // Return null while loading - shows placeholder or empty
            return null;
        }

        // Fallback: synchronous loading with DecodeToWidth for smaller memory
        return LoadSynchronous(path);
    }

    private async Task LoadThumbnailAsync(string path)
    {
        if (ThumbnailService is null)
            return;

        try
        {
            await ThumbnailService.LoadThumbnailAsync(path, DefaultThumbnailWidth);
            // Note: The ViewModel's Thumbnail property will trigger binding refresh
        }
        catch
        {
            // Ignore loading errors
        }
    }

    /// <summary>
    /// Loads the full resolution image without any scaling.
    /// </summary>
    private static Bitmap? LoadFullResolution(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap? LoadSynchronous(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            // Use DecodeToWidth for smaller memory footprint
            return Bitmap.DecodeToWidth(stream, DefaultThumbnailWidth, BitmapInterpolationMode.MediumQuality);
        }
        catch
        {
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Multi-value converter that uses ThumbnailPath and ThumbnailVersion for cache-busting.
/// Values[0] = ThumbnailPath (string)
/// Values[1] = ThumbnailVersion (int) - used to trigger re-evaluation when thumbnail is loaded
/// </summary>
public class ThumbnailMultiConverter : IMultiValueConverter
{
    public static readonly ThumbnailMultiConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 1)
            return null;

        var path = values[0] as string;
        // values[1] is ThumbnailVersion - just having it in the binding triggers re-evaluation

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        var thumbnailService = PathToBitmapConverter.ThumbnailService;
        
        if (thumbnailService is not null)
        {
            // Check cache first
            if (thumbnailService.TryGetCached(path, out var cached))
                return cached;

            // Trigger async load
            _ = LoadAsync(path, thumbnailService);
            return null;
        }

        // Fallback to sync load
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, PathToBitmapConverter.DefaultThumbnailWidth, BitmapInterpolationMode.MediumQuality);
        }
        catch
        {
            return null;
        }
    }

    private static async Task LoadAsync(string path, IThumbnailService service)
    {
        try
        {
            await service.LoadThumbnailAsync(path, PathToBitmapConverter.DefaultThumbnailWidth);
        }
        catch
        {
            // Ignore
        }
    }
}
