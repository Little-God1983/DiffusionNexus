using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Implementation of <see cref="IDatasetState"/> providing centralized state management
/// for the LoRA Dataset Helper. This service acts as the single source of truth for
/// all dataset-related state and coordinates with the <see cref="IDatasetEventAggregator"/>
/// to notify subscribers of changes.
/// 
/// <para>
/// <b>Architecture Notes:</b>
/// <list type="bullet">
/// <item>Registered as a singleton in the DI container</item>
/// <item>All state modifications should be made through this service</item>
/// <item>Automatically publishes events via EventAggregator when state changes</item>
/// <item>Observable properties for data binding support</item>
/// </list>
/// </para>
/// </summary>
public sealed class DatasetStateService : ObservableObject, IDatasetState
{
    private readonly IDatasetEventAggregator _eventAggregator;

    private DatasetCardViewModel? _activeDataset;
    private int _selectedVersion = 1;
    private bool _isViewingDataset;
    private bool _isStorageConfigured;
    private bool _flattenVersions;
    private bool _isLoading;
    private string? _statusMessage;
    private bool _hasUnsavedChanges;
    private bool _isFileDialogOpen;
    private int _selectedTabIndex;
    private int _selectionCount;
    private DatasetImageViewModel? _lastClickedImage;
    private DatasetCardViewModel? _selectedEditorDataset;
    private EditorVersionItem? _selectedEditorVersion;
    private DatasetImageViewModel? _selectedEditorImage;

    /// <summary>
    /// Creates a new instance of <see cref="DatasetStateService"/>.
    /// </summary>
    /// <param name="eventAggregator">The event aggregator for publishing state changes.</param>
    public DatasetStateService(IDatasetEventAggregator eventAggregator)
    {
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));

        // Initialize collections
        Datasets = [];
        GroupedDatasets = [];
        DatasetImages = [];
        AvailableCategories = [];
        AvailableVersions = [];
        EditorVersionItems = [];
        EditorDatasetImages = [];

        // Subscribe to collection changes for HasNoImages
        DatasetImages.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasNoImages));
            RaiseStateChanged(nameof(HasNoImages));
        };
    }

    #region Dataset State Properties

    /// <inheritdoc/>
    public DatasetCardViewModel? ActiveDataset
    {
        get => _activeDataset;
        private set
        {
            var previous = _activeDataset;
            if (SetProperty(ref _activeDataset, value))
            {
                OnPropertyChanged(nameof(IsViewingDataset));
                OnPropertyChanged(nameof(HasNoImages));
                RaiseStateChanged(nameof(ActiveDataset));

                // Publish event
                _eventAggregator.PublishActiveDatasetChanged(new ActiveDatasetChangedEventArgs
                {
                    Dataset = value,
                    PreviousDataset = previous
                });
            }
        }
    }

    /// <inheritdoc/>
    public int SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (SetProperty(ref _selectedVersion, value))
            {
                RaiseStateChanged(nameof(SelectedVersion));
            }
        }
    }

    /// <inheritdoc/>
    public bool IsViewingDataset
    {
        get => _isViewingDataset;
        private set
        {
            if (SetProperty(ref _isViewingDataset, value))
            {
                OnPropertyChanged(nameof(HasNoImages));
                RaiseStateChanged(nameof(IsViewingDataset));
            }
        }
    }

    /// <inheritdoc/>
    public bool IsStorageConfigured
    {
        get => _isStorageConfigured;
        private set
        {
            if (SetProperty(ref _isStorageConfigured, value))
            {
                RaiseStateChanged(nameof(IsStorageConfigured));
            }
        }
    }

    /// <inheritdoc/>
    public bool FlattenVersions
    {
        get => _flattenVersions;
        set
        {
            if (SetProperty(ref _flattenVersions, value))
            {
                RaiseStateChanged(nameof(FlattenVersions));
            }
        }
    }

    #endregion

    #region Collections

    /// <inheritdoc/>
    public ObservableCollection<DatasetCardViewModel> Datasets { get; }

    /// <inheritdoc/>
    public ObservableCollection<DatasetGroupViewModel> GroupedDatasets { get; }

    /// <inheritdoc/>
    public ObservableCollection<DatasetImageViewModel> DatasetImages { get; }

    /// <inheritdoc/>
    public ObservableCollection<DatasetCategoryViewModel> AvailableCategories { get; }

    /// <inheritdoc/>
    public ObservableCollection<int> AvailableVersions { get; }

    #endregion

    #region Selection State

    /// <inheritdoc/>
    public int SelectionCount
    {
        get => _selectionCount;
        private set
        {
            if (SetProperty(ref _selectionCount, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                RaiseStateChanged(nameof(SelectionCount));
            }
        }
    }

    /// <inheritdoc/>
    public bool HasSelection => _selectionCount > 0;

    /// <inheritdoc/>
    public DatasetImageViewModel? LastClickedImage
    {
        get => _lastClickedImage;
        set => SetProperty(ref _lastClickedImage, value);
    }

    #endregion

    #region Image Edit State

    /// <inheritdoc/>
    public DatasetCardViewModel? SelectedEditorDataset
    {
        get => _selectedEditorDataset;
        set
        {
            if (SetProperty(ref _selectedEditorDataset, value))
            {
                RaiseStateChanged(nameof(SelectedEditorDataset));
            }
        }
    }

    /// <inheritdoc/>
    public EditorVersionItem? SelectedEditorVersion
    {
        get => _selectedEditorVersion;
        set
        {
            if (SetProperty(ref _selectedEditorVersion, value))
            {
                RaiseStateChanged(nameof(SelectedEditorVersion));
            }
        }
    }

    /// <inheritdoc/>
    public DatasetImageViewModel? SelectedEditorImage
    {
        get => _selectedEditorImage;
        set
        {
            if (SetProperty(ref _selectedEditorImage, value))
            {
                RaiseStateChanged(nameof(SelectedEditorImage));
            }
        }
    }

    /// <inheritdoc/>
    public ObservableCollection<EditorVersionItem> EditorVersionItems { get; }

    /// <inheritdoc/>
    public ObservableCollection<DatasetImageViewModel> EditorDatasetImages { get; }

    #endregion

    #region UI State

    /// <inheritdoc/>
    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RaiseStateChanged(nameof(IsLoading));
            }
        }
    }

    /// <inheritdoc/>
    public string? StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                RaiseStateChanged(nameof(StatusMessage));
            }
        }
    }

    /// <inheritdoc/>
    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        set
        {
            if (SetProperty(ref _hasUnsavedChanges, value))
            {
                RaiseStateChanged(nameof(HasUnsavedChanges));
            }
        }
    }

    /// <inheritdoc/>
    public bool IsFileDialogOpen
    {
        get => _isFileDialogOpen;
        set
        {
            if (SetProperty(ref _isFileDialogOpen, value))
            {
                RaiseStateChanged(nameof(IsFileDialogOpen));
            }
        }
    }

    /// <inheritdoc/>
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (SetProperty(ref _selectedTabIndex, value))
            {
                RaiseStateChanged(nameof(SelectedTabIndex));
            }
        }
    }

    /// <inheritdoc/>
    public bool HasNoImages => IsViewingDataset && DatasetImages.Count == 0;

    #endregion

    #region State Modification Methods

    /// <inheritdoc/>
    public void SetActiveDataset(DatasetCardViewModel? dataset)
    {
        ActiveDataset = dataset;
        IsViewingDataset = dataset is not null;

        if (dataset is not null)
        {
            SelectedVersion = dataset.CurrentVersion;
        }
    }

    /// <inheritdoc/>
    public void SetStorageConfigured(bool isConfigured)
    {
        IsStorageConfigured = isConfigured;
    }

    /// <inheritdoc/>
    public void UpdateSelectionCount()
    {
        SelectionCount = DatasetImages.Count(i => i.IsSelected);
    }

    /// <inheritdoc/>
    public void ClearSelectionSilent()
    {
        foreach (var image in DatasetImages)
        {
            image.IsSelected = false;
        }
        SelectionCount = 0;
    }

    #endregion

    #region Events

    /// <inheritdoc/>
    public event EventHandler<DatasetStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Raises the <see cref="StateChanged"/> event.
    /// </summary>
    private void RaiseStateChanged(string propertyName)
    {
        StateChanged?.Invoke(this, new DatasetStateChangedEventArgs { PropertyName = propertyName });
    }

    #endregion
}
