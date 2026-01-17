using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.Services;

namespace DiffusionNexus.UI.ViewModels.Tabs;

/// <summary>
/// ViewModel for the Image Compare tab in the LoRA Dataset Helper.
/// Wraps ImageComparerViewModel and provides tab-level functionality.
/// 
/// <para>
/// <b>Responsibilities:</b>
/// <list type="bullet">
/// <item>Managing the image comparison state</item>
/// <item>Handling navigation from other tabs with pre-loaded images</item>
/// <item>Providing clear/reset functionality</item>
/// </list>
/// </para>
/// 
/// <para>
/// <b>Event Integration:</b>
/// Subscribes to:
/// <list type="bullet">
/// <item>NavigateToImageCompareRequested - to load images sent from Dataset Management or Image Editor</item>
/// </list>
/// </para>
/// 
/// <para>
/// <b>Disposal:</b>
/// Implements <see cref="IDisposable"/> to properly unsubscribe from events.
/// </para>
/// </summary>
public partial class ImageCompareTabViewModel : ObservableObject, IDisposable
{
    private readonly IDatasetEventAggregator _eventAggregator;
    private bool _disposed;

    /// <summary>
    /// The inner ViewModel that handles the actual image comparison UI.
    /// </summary>
    public ImageComparerViewModel Comparer { get; } = new();

    /// <summary>
    /// Creates a new instance of ImageCompareTabViewModel.
    /// </summary>
    /// <param name="eventAggregator">The event aggregator for inter-component communication.</param>
    public ImageCompareTabViewModel(IDatasetEventAggregator eventAggregator)
    {
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));

        // Subscribe to navigation events
        _eventAggregator.NavigateToImageCompareRequested += OnNavigateToImageCompare;
    }

    /// <summary>
    /// Design-time constructor.
    /// </summary>
    public ImageCompareTabViewModel() : this(null!)
    {
    }

    /// <summary>
    /// Handles navigation from other tabs with image paths.
    /// </summary>
    private void OnNavigateToImageCompare(object? sender, NavigateToImageCompareEventArgs e)
    {
        Comparer.LoadImages(e.BottomImagePath, e.TopImagePath);
    }

    /// <summary>
    /// Loads images programmatically from file paths.
    /// </summary>
    /// <param name="bottomPath">Path to the bottom (left/original) image.</param>
    /// <param name="topPath">Path to the top (right/new) image.</param>
    public void LoadImages(string? bottomPath, string? topPath)
    {
        Comparer.LoadImages(bottomPath, topPath);
    }

    /// <summary>
    /// Clears all images from the comparison view.
    /// </summary>
    [RelayCommand]
    private void ClearImages()
    {
        Comparer.LoadImages(null, null);
    }

    /// <summary>
    /// Resets the slider to the center position.
    /// </summary>
    [RelayCommand]
    private void ResetSlider()
    {
        Comparer.SliderPosition = 0.5;
    }

    #region IDisposable

    /// <summary>
    /// Releases all resources and unsubscribes from events.
    /// </summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases resources used by this ViewModel.
    /// </summary>
    /// <param name="disposing">True if called from Dispose(), false if from finalizer.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            // Unsubscribe from events to prevent memory leaks
            if (_eventAggregator is not null)
            {
                _eventAggregator.NavigateToImageCompareRequested -= OnNavigateToImageCompare;
            }
        }

        _disposed = true;
    }

    #endregion
}
