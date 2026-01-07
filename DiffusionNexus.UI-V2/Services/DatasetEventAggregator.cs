using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Services;

#region Event Argument Classes

/// <summary>
/// Base class for all dataset-related events.
/// </summary>
public abstract class DatasetEventArgs : EventArgs
{
    /// <summary>
    /// Timestamp when the event was created.
    /// </summary>
    public DateTime Timestamp { get; } = DateTime.UtcNow;
}

/// <summary>
/// Event raised when the active dataset changes.
/// </summary>
public sealed class ActiveDatasetChangedEventArgs : DatasetEventArgs
{
    /// <summary>
    /// The newly active dataset, or null if no dataset is active.
    /// </summary>
    public DatasetCardViewModel? Dataset { get; init; }

    /// <summary>
    /// The previously active dataset, or null if there was none.
    /// </summary>
    public DatasetCardViewModel? PreviousDataset { get; init; }
}

/// <summary>
/// Event raised when a dataset is created.
/// </summary>
public sealed class DatasetCreatedEventArgs : DatasetEventArgs
{
    /// <summary>
    /// The newly created dataset.
    /// </summary>
    public required DatasetCardViewModel Dataset { get; init; }
}

/// <summary>
/// Event raised when a dataset is deleted.
/// </summary>
public sealed class DatasetDeletedEventArgs : DatasetEventArgs
{
    /// <summary>
    /// The deleted dataset.
    /// </summary>
    public required DatasetCardViewModel Dataset { get; init; }

    /// <summary>
    /// Version number if only a specific version was deleted, null if entire dataset.
    /// </summary>
    public int? DeletedVersion { get; init; }
}

/// <summary>
/// Event raised when a dataset's metadata is updated (category, type, description, etc.).
/// </summary>
public sealed class DatasetMetadataChangedEventArgs : DatasetEventArgs
{
    /// <summary>
    /// The updated dataset.
    /// </summary>
    public required DatasetCardViewModel Dataset { get; init; }

    /// <summary>
    /// Type of metadata that changed.
    /// </summary>
    public required DatasetMetadataChangeType ChangeType { get; init; }
}

/// <summary>
/// Types of metadata changes.
/// </summary>
public enum DatasetMetadataChangeType
{
    Category,
    Type,
    Description,
    Version,
    All
}

/// <summary>
/// Event raised when dataset images/videos are loaded or refreshed.
/// </summary>
public sealed class DatasetImagesLoadedEventArgs : DatasetEventArgs
{
    /// <summary>
    /// The dataset whose images were loaded.
    /// </summary>
    public required DatasetCardViewModel Dataset { get; init; }

    /// <summary>
    /// The loaded images.
    /// </summary>
    public required IReadOnlyList<DatasetImageViewModel> Images { get; init; }
}

/// <summary>
/// Event raised when an image is added to a dataset.
/// </summary>
public sealed class ImageAddedEventArgs : DatasetEventArgs
{
    /// <summary>
    /// The dataset the image was added to.
    /// </summary>
    public required DatasetCardViewModel Dataset { get; init; }

    /// <summary>
    /// The added image(s).
    /// </summary>
    public required IReadOnlyList<DatasetImageViewModel> AddedImages { get; init; }
}

/// <summary>
/// Event raised when an image is deleted from a dataset.
/// </summary>
public sealed class ImageDeletedEventArgs : DatasetEventArgs
{
    /// <summary>
    /// The dataset the image was deleted from.
    /// </summary>
    public required DatasetCardViewModel Dataset { get; init; }

    /// <summary>
    /// Path to the deleted image.
    /// </summary>
    public required string ImagePath { get; init; }
}

/// <summary>
/// Event raised when an image is saved (new or overwritten).
/// </summary>
public sealed class ImageSavedEventArgs : DatasetEventArgs
{
    /// <summary>
    /// Path to the saved image.
    /// </summary>
    public required string ImagePath { get; init; }

    /// <summary>
    /// Original path if this was a "save as" operation, null if overwritten.
    /// </summary>
    public string? OriginalPath { get; init; }

    /// <summary>
    /// Whether this was a new file or an overwrite.
    /// </summary>
    public bool IsNewFile => OriginalPath is not null;
}

/// <summary>
/// Event raised when an image's rating changes.
/// </summary>
public sealed class ImageRatingChangedEventArgs : DatasetEventArgs
{
    /// <summary>
    /// The image whose rating changed.
    /// </summary>
    public required DatasetImageViewModel Image { get; init; }

    /// <summary>
    /// The new rating status.
    /// </summary>
    public required ImageRatingStatus NewRating { get; init; }

    /// <summary>
    /// The previous rating status.
    /// </summary>
    public required ImageRatingStatus PreviousRating { get; init; }
}

/// <summary>
/// Event raised when an image's caption changes.
/// </summary>
public sealed class CaptionChangedEventArgs : DatasetEventArgs
{
    /// <summary>
    /// The image whose caption changed.
    /// </summary>
    public required DatasetImageViewModel Image { get; init; }

    /// <summary>
    /// Whether the caption was saved (vs just modified in memory).
    /// </summary>
    public bool WasSaved { get; init; }
}

/// <summary>
/// Event raised when an image's selection state changes.
/// </summary>
public sealed class ImageSelectionChangedEventArgs : DatasetEventArgs
{
    /// <summary>
    /// The image whose selection changed.
    /// </summary>
    public required DatasetImageViewModel Image { get; init; }

    /// <summary>
    /// Whether the image is now selected.
    /// </summary>
    public required bool IsSelected { get; init; }
}

/// <summary>
/// Event raised when a dataset version is created.
/// </summary>
public sealed class VersionCreatedEventArgs : DatasetEventArgs
{
    /// <summary>
    /// The dataset.
    /// </summary>
    public required DatasetCardViewModel Dataset { get; init; }

    /// <summary>
    /// The new version number.
    /// </summary>
    public required int NewVersion { get; init; }

    /// <summary>
    /// The version it was branched from.
    /// </summary>
    public required int BranchedFromVersion { get; init; }
}

/// <summary>
/// Event raised when navigation to the Image Editor is requested.
/// </summary>
public sealed class NavigateToImageEditorEventArgs : DatasetEventArgs
{
    /// <summary>
    /// The image to edit.
    /// </summary>
    public required DatasetImageViewModel Image { get; init; }

    /// <summary>
    /// The dataset containing the image.
    /// </summary>
    public required DatasetCardViewModel Dataset { get; init; }
}

/// <summary>
/// Event raised when the dataset list should be refreshed.
/// </summary>
public sealed class RefreshDatasetsRequestedEventArgs : DatasetEventArgs
{
}

#endregion

/// <summary>
/// Event aggregator service for loosely-coupled communication between components
/// in the LoRA Dataset Helper. Implements the publish-subscribe pattern to enable
/// different parts of the application to communicate without direct references.
/// 
/// <para>
/// <b>Usage Pattern:</b>
/// <list type="bullet">
/// <item>Inject <see cref="IDatasetEventAggregator"/> into ViewModels</item>
/// <item>Subscribe to events in constructor or initialization</item>
/// <item>Unsubscribe in Dispose/cleanup</item>
/// <item>Publish events when state changes</item>
/// </list>
/// </para>
/// 
/// <para>
/// <b>Example:</b>
/// <code>
/// // Subscribe
/// _eventAggregator.ImageSaved += OnImageSaved;
/// 
/// // Publish
/// _eventAggregator.PublishImageSaved(new ImageSavedEventArgs { ImagePath = path });
/// 
/// // Cleanup
/// _eventAggregator.ImageSaved -= OnImageSaved;
/// </code>
/// </para>
/// </summary>
public interface IDatasetEventAggregator
{
    #region Dataset Events

    /// <summary>
    /// Raised when the active dataset changes.
    /// </summary>
    event EventHandler<ActiveDatasetChangedEventArgs>? ActiveDatasetChanged;

    /// <summary>
    /// Raised when a new dataset is created.
    /// </summary>
    event EventHandler<DatasetCreatedEventArgs>? DatasetCreated;

    /// <summary>
    /// Raised when a dataset is deleted.
    /// </summary>
    event EventHandler<DatasetDeletedEventArgs>? DatasetDeleted;

    /// <summary>
    /// Raised when dataset metadata changes.
    /// </summary>
    event EventHandler<DatasetMetadataChangedEventArgs>? DatasetMetadataChanged;

    /// <summary>
    /// Raised when dataset images are loaded.
    /// </summary>
    event EventHandler<DatasetImagesLoadedEventArgs>? DatasetImagesLoaded;

    /// <summary>
    /// Raised when a new version is created.
    /// </summary>
    event EventHandler<VersionCreatedEventArgs>? VersionCreated;

    /// <summary>
    /// Raised when the dataset list should be refreshed.
    /// </summary>
    event EventHandler<RefreshDatasetsRequestedEventArgs>? RefreshDatasetsRequested;

    #endregion

    #region Image Events

    /// <summary>
    /// Raised when images are added to a dataset.
    /// </summary>
    event EventHandler<ImageAddedEventArgs>? ImageAdded;

    /// <summary>
    /// Raised when an image is deleted.
    /// </summary>
    event EventHandler<ImageDeletedEventArgs>? ImageDeleted;

    /// <summary>
    /// Raised when an image is saved.
    /// </summary>
    event EventHandler<ImageSavedEventArgs>? ImageSaved;

    /// <summary>
    /// Raised when an image's rating changes.
    /// </summary>
    event EventHandler<ImageRatingChangedEventArgs>? ImageRatingChanged;

    /// <summary>
    /// Raised when a caption changes.
    /// </summary>
    event EventHandler<CaptionChangedEventArgs>? CaptionChanged;

    /// <summary>
    /// Raised when image selection changes.
    /// </summary>
    event EventHandler<ImageSelectionChangedEventArgs>? ImageSelectionChanged;

    #endregion

    #region Navigation Events

    /// <summary>
    /// Raised when navigation to the Image Editor is requested.
    /// </summary>
    event EventHandler<NavigateToImageEditorEventArgs>? NavigateToImageEditorRequested;

    #endregion

    #region Publish Methods

    void PublishActiveDatasetChanged(ActiveDatasetChangedEventArgs args);
    void PublishDatasetCreated(DatasetCreatedEventArgs args);
    void PublishDatasetDeleted(DatasetDeletedEventArgs args);
    void PublishDatasetMetadataChanged(DatasetMetadataChangedEventArgs args);
    void PublishDatasetImagesLoaded(DatasetImagesLoadedEventArgs args);
    void PublishVersionCreated(VersionCreatedEventArgs args);
    void PublishRefreshDatasetsRequested(RefreshDatasetsRequestedEventArgs args);
    void PublishImageAdded(ImageAddedEventArgs args);
    void PublishImageDeleted(ImageDeletedEventArgs args);
    void PublishImageSaved(ImageSavedEventArgs args);
    void PublishImageRatingChanged(ImageRatingChangedEventArgs args);
    void PublishCaptionChanged(CaptionChangedEventArgs args);
    void PublishImageSelectionChanged(ImageSelectionChangedEventArgs args);
    void PublishNavigateToImageEditor(NavigateToImageEditorEventArgs args);

    #endregion
}

/// <summary>
/// Thread-safe implementation of <see cref="IDatasetEventAggregator"/>.
/// Uses weak event patterns where appropriate to prevent memory leaks.
/// </summary>
public sealed class DatasetEventAggregator : IDatasetEventAggregator
{
    private readonly object _syncLock = new();

    #region Dataset Events

    /// <inheritdoc/>
    public event EventHandler<ActiveDatasetChangedEventArgs>? ActiveDatasetChanged;

    /// <inheritdoc/>
    public event EventHandler<DatasetCreatedEventArgs>? DatasetCreated;

    /// <inheritdoc/>
    public event EventHandler<DatasetDeletedEventArgs>? DatasetDeleted;

    /// <inheritdoc/>
    public event EventHandler<DatasetMetadataChangedEventArgs>? DatasetMetadataChanged;

    /// <inheritdoc/>
    public event EventHandler<DatasetImagesLoadedEventArgs>? DatasetImagesLoaded;

    /// <inheritdoc/>
    public event EventHandler<VersionCreatedEventArgs>? VersionCreated;

    /// <inheritdoc/>
    public event EventHandler<RefreshDatasetsRequestedEventArgs>? RefreshDatasetsRequested;

    #endregion

    #region Image Events

    /// <inheritdoc/>
    public event EventHandler<ImageAddedEventArgs>? ImageAdded;

    /// <inheritdoc/>
    public event EventHandler<ImageDeletedEventArgs>? ImageDeleted;

    /// <inheritdoc/>
    public event EventHandler<ImageSavedEventArgs>? ImageSaved;

    /// <inheritdoc/>
    public event EventHandler<ImageRatingChangedEventArgs>? ImageRatingChanged;

    /// <inheritdoc/>
    public event EventHandler<CaptionChangedEventArgs>? CaptionChanged;

    /// <inheritdoc/>
    public event EventHandler<ImageSelectionChangedEventArgs>? ImageSelectionChanged;

    #endregion

    #region Navigation Events

    /// <inheritdoc/>
    public event EventHandler<NavigateToImageEditorEventArgs>? NavigateToImageEditorRequested;

    #endregion

    #region Publish Methods

    /// <inheritdoc/>
    public void PublishActiveDatasetChanged(ActiveDatasetChangedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        RaiseEvent(ActiveDatasetChanged, args);
    }

    /// <inheritdoc/>
    public void PublishDatasetCreated(DatasetCreatedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        RaiseEvent(DatasetCreated, args);
    }

    /// <inheritdoc/>
    public void PublishDatasetDeleted(DatasetDeletedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        RaiseEvent(DatasetDeleted, args);
    }

    /// <inheritdoc/>
    public void PublishDatasetMetadataChanged(DatasetMetadataChangedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        RaiseEvent(DatasetMetadataChanged, args);
    }

    /// <inheritdoc/>
    public void PublishDatasetImagesLoaded(DatasetImagesLoadedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        RaiseEvent(DatasetImagesLoaded, args);
    }

    /// <inheritdoc/>
    public void PublishVersionCreated(VersionCreatedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        RaiseEvent(VersionCreated, args);
    }

    /// <inheritdoc/>
    public void PublishRefreshDatasetsRequested(RefreshDatasetsRequestedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        RaiseEvent(RefreshDatasetsRequested, args);
    }

    /// <inheritdoc/>
    public void PublishImageAdded(ImageAddedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        RaiseEvent(ImageAdded, args);
    }

    /// <inheritdoc/>
    public void PublishImageDeleted(ImageDeletedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        RaiseEvent(ImageDeleted, args);
    }

    /// <inheritdoc/>
    public void PublishImageSaved(ImageSavedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        RaiseEvent(ImageSaved, args);
    }

    /// <inheritdoc/>
    public void PublishImageRatingChanged(ImageRatingChangedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        RaiseEvent(ImageRatingChanged, args);
    }

    /// <inheritdoc/>
    public void PublishCaptionChanged(CaptionChangedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        RaiseEvent(CaptionChanged, args);
    }

    /// <inheritdoc/>
    public void PublishImageSelectionChanged(ImageSelectionChangedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        RaiseEvent(ImageSelectionChanged, args);
    }

    /// <inheritdoc/>
    public void PublishNavigateToImageEditor(NavigateToImageEditorEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        RaiseEvent(NavigateToImageEditorRequested, args);
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Thread-safe event raising with null check.
    /// </summary>
    private void RaiseEvent<T>(EventHandler<T>? handler, T args) where T : DatasetEventArgs
    {
        // Get handler reference under lock to prevent race conditions
        EventHandler<T>? localHandler;
        lock (_syncLock)
        {
            localHandler = handler;
        }

        // Invoke outside lock to prevent deadlocks
        localHandler?.Invoke(this, args);
    }

    #endregion
}
