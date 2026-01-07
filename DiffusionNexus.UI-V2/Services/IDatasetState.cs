using System.Collections.ObjectModel;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Provides shared state for dataset-related operations across all tabs and components
/// in the LoRA Dataset Helper. This service maintains the single source of truth for:
/// <list type="bullet">
/// <item>Currently active dataset</item>
/// <item>Collection of all datasets</item>
/// <item>Collection of images in the active dataset</item>
/// <item>Selection state</item>
/// <item>View mode preferences</item>
/// </list>
/// 
/// <para>
/// <b>Design Pattern:</b> This implements the Shared State pattern, providing a central
/// location for state that needs to be accessed by multiple ViewModels. Changes to state
/// are published via <see cref="IDatasetEventAggregator"/> to ensure all subscribers
/// are notified.
/// </para>
/// 
/// <para>
/// <b>Thread Safety:</b> All state modifications should be performed on the UI thread.
/// The service does not provide internal synchronization for performance reasons.
/// </para>
/// </summary>
public interface IDatasetState
{
    #region Dataset State

    /// <summary>
    /// Gets the currently active dataset, or null if no dataset is open.
    /// </summary>
    DatasetCardViewModel? ActiveDataset { get; }

    /// <summary>
    /// Gets or sets the currently selected version for the active dataset.
    /// </summary>
    int SelectedVersion { get; set; }

    /// <summary>
    /// Gets whether a dataset is currently being viewed (vs the overview).
    /// </summary>
    bool IsViewingDataset { get; }

    /// <summary>
    /// Gets whether dataset storage is configured.
    /// </summary>
    bool IsStorageConfigured { get; }

    /// <summary>
    /// Gets or sets whether to flatten versions in the overview.
    /// </summary>
    bool FlattenVersions { get; set; }

    #endregion

    #region Collections

    /// <summary>
    /// Gets the collection of all datasets.
    /// </summary>
    ObservableCollection<DatasetCardViewModel> Datasets { get; }

    /// <summary>
    /// Gets the collection of datasets grouped by category.
    /// </summary>
    ObservableCollection<DatasetGroupViewModel> GroupedDatasets { get; }

    /// <summary>
    /// Gets the collection of images in the active dataset.
    /// </summary>
    ObservableCollection<DatasetImageViewModel> DatasetImages { get; }

    /// <summary>
    /// Gets the available categories.
    /// </summary>
    ObservableCollection<DatasetCategoryViewModel> AvailableCategories { get; }

    /// <summary>
    /// Gets the available versions for the active dataset.
    /// </summary>
    ObservableCollection<int> AvailableVersions { get; }

    #endregion

    #region Selection State

    /// <summary>
    /// Gets the number of currently selected images.
    /// </summary>
    int SelectionCount { get; }

    /// <summary>
    /// Gets whether any images are currently selected.
    /// </summary>
    bool HasSelection { get; }

    /// <summary>
    /// Gets or sets the last clicked image for range selection.
    /// </summary>
    DatasetImageViewModel? LastClickedImage { get; set; }

    #endregion

    #region Image Edit State

    /// <summary>
    /// Gets or sets the currently selected dataset for the Image Editor tab.
    /// </summary>
    DatasetCardViewModel? SelectedEditorDataset { get; set; }

    /// <summary>
    /// Gets or sets the currently selected version for the Image Editor tab.
    /// </summary>
    EditorVersionItem? SelectedEditorVersion { get; set; }

    /// <summary>
    /// Gets or sets the currently selected image in the Image Editor tab.
    /// </summary>
    DatasetImageViewModel? SelectedEditorImage { get; set; }

    /// <summary>
    /// Gets the collection of version items for the Image Editor.
    /// </summary>
    ObservableCollection<EditorVersionItem> EditorVersionItems { get; }

    /// <summary>
    /// Gets the collection of images for the Image Editor.
    /// </summary>
    ObservableCollection<DatasetImageViewModel> EditorDatasetImages { get; }

    #endregion

    #region UI State

    /// <summary>
    /// Gets or sets whether data is currently loading.
    /// </summary>
    bool IsLoading { get; set; }

    /// <summary>
    /// Gets or sets the current status message.
    /// </summary>
    string? StatusMessage { get; set; }

    /// <summary>
    /// Gets or sets whether there are unsaved caption changes.
    /// </summary>
    bool HasUnsavedChanges { get; set; }

    /// <summary>
    /// Gets or sets whether a file dialog is currently open.
    /// </summary>
    bool IsFileDialogOpen { get; set; }

    /// <summary>
    /// Gets or sets the selected tab index.
    /// </summary>
    int SelectedTabIndex { get; set; }

    #endregion

    #region State Modification Methods

    /// <summary>
    /// Sets the active dataset and updates related state.
    /// </summary>
    /// <param name="dataset">The dataset to make active, or null to go to overview.</param>
    void SetActiveDataset(DatasetCardViewModel? dataset);

    /// <summary>
    /// Sets the storage configuration state.
    /// </summary>
    /// <param name="isConfigured">Whether storage is configured.</param>
    void SetStorageConfigured(bool isConfigured);

    /// <summary>
    /// Updates the selection count based on current selected images.
    /// </summary>
    void UpdateSelectionCount();

    /// <summary>
    /// Clears all image selections without showing a status message.
    /// </summary>
    void ClearSelectionSilent();

    /// <summary>
    /// Checks if the active dataset has no images.
    /// </summary>
    bool HasNoImages { get; }

    #endregion

    #region Events

    /// <summary>
    /// Raised when a state property changes.
    /// </summary>
    event EventHandler<DatasetStateChangedEventArgs>? StateChanged;

    #endregion
}

/// <summary>
/// Event args for state changes in <see cref="IDatasetState"/>.
/// </summary>
public sealed class DatasetStateChangedEventArgs : EventArgs
{
    /// <summary>
    /// The name of the property that changed.
    /// </summary>
    public required string PropertyName { get; init; }
}
