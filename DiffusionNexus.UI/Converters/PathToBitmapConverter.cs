using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using DiffusionNexus.UI.Services;
using System.Globalization;

namespace DiffusionNexus.UI.Converters;

/// <summary>
/// Converts a file path string to a Bitmap for display in Image controls.
/// <para>
/// <b>Sync-only:</b> This converter only returns cache hits or performs synchronous fallback loading.
/// It does NOT fire async loads — ViewModels with a <c>Thumbnail</c> property should be bound
/// directly instead of using this converter for thumbnail-mode images.
/// </para>
/// <para>
/// <b>ConverterParameter options:</b>
/// <list type="bullet">
/// <item>"full" or "fullres": Load full resolution image synchronously (for viewers/editors)</item>
/// <item>null or "thumbnail": Return cached thumbnail if available, otherwise sync fallback</item>
/// </list>
/// </para>
/// </summary>
public class PathToBitmapConverter : IValueConverter
{
    /// <summary>
    /// Singleton instance for use in XAML.
    /// </summary>
    public static readonly PathToBitmapConverter Instance = new();

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
            return LoadFullResolution(path);
        }

        // Thumbnail mode: return from cache only — ViewModels handle async loading via their Thumbnail property
        if (ThumbnailOrchestrator is not null &&
            ThumbnailOrchestrator.TryGetCached(path, out var cached) &&
            cached is not null)
        {
            return cached;
        }

        // Sync fallback when orchestrator is not available or cache miss
        return LoadSynchronous(path);
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
/// Values[1] = ThumbnailVersion (int) - having it in the binding triggers re-evaluation
/// <para>
/// Sync-only: returns cache hit or sync fallback. See <see cref="PathToBitmapConverter"/>.
/// </para>
/// </summary>
public class ThumbnailMultiConverter : IMultiValueConverter
{
    public static readonly ThumbnailMultiConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 1)
            return null;

        var path = values[0] as string;

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        var orchestrator = PathToBitmapConverter.ThumbnailOrchestrator;

        if (orchestrator is not null &&
            orchestrator.TryGetCached(path, out var cached) &&
            cached is not null)
        {
            return cached;
        }

        // Sync fallback
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
}
