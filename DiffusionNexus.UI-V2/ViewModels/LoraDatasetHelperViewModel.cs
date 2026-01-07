using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels.Tabs;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Main ViewModel for the LoRA Dataset Helper module.
/// Acts as a coordinator between the tab ViewModels and provides shared state.
/// 
/// <para>
/// <b>Architecture:</b>
/// This ViewModel follows the Coordinator pattern, delegating most functionality
/// to specialized tab ViewModels:
/// <list type="bullet">
/// <item><see cref="DatasetManagementViewModel"/> - Dataset listing, creation, image management</item>
/// <item><see cref="ImageEditTabViewModel"/> - Image editing with the ImageEditorControl</item>
/// <item><see cref="AutoScaleCropTabViewModel"/> - Batch image cropping to aspect ratio buckets</item>
/// </list>
/// </para>
/// 
/// <para>
/// <b>Event-Driven Communication:</b>
/// All tab ViewModels communicate through <see cref="IDatasetEventAggregator"/>,
/// ensuring loose coupling and proper state synchronization across tabs.
/// </para>
/// 
/// <para>
/// <b>Disposal:</b>
/// Implements <see cref="IDisposable"/> to properly unsubscribe from events.
/// The view should dispose this ViewModel when unloaded.
/// </para>
/// </summary>
public partial class LoraDatasetHelperViewModel : ViewModelBase, IDialogServiceAware, IDisposable
{
    private readonly IDatasetEventAggregator _eventAggregator;
    private readonly IDatasetState _state;
    private bool _disposed;

    private int _selectedTabIndex;

    /// <summary>
    /// Gets or sets the dialog service for showing dialogs.
    /// </summary>
    public IDialogService? DialogService { get; set; }

    #region Tab ViewModels

    /// <summary>
    /// ViewModel for the Dataset Management tab.
    /// </summary>
    public DatasetManagementViewModel DatasetManagement { get; }

    /// <summary>
    /// ViewModel for the Image Edit tab.
    /// </summary>
    public ImageEditTabViewModel ImageEdit { get; }

    /// <summary>
    /// ViewModel for the Auto Scale/Crop tab.
    /// </summary>
    public AutoScaleCropTabViewModel AutoScaleCrop { get; }

    #endregion

    #region Observable Properties

    /// <summary>
    /// Selected tab index for programmatic tab switching.
    /// </summary>
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (SetProperty(ref _selectedTabIndex, value))
            {
                _state.SelectedTabIndex = value;
            }
        }
    }

    /// <summary>
    /// Status message to display to the user.
    /// </summary>
    public string? StatusMessage => _state.StatusMessage;

    #endregion

    #region Constructors

    /// <summary>
    /// Creates a new instance of LoraDatasetHelperViewModel.
    /// </summary>
    /// <param name="settingsService">The application settings service.</param>
    /// <param name="eventAggregator">The event aggregator for inter-component communication.</param>
    /// <param name="state">The shared dataset state service.</param>
    /// <param name="videoThumbnailService">Optional video thumbnail service.</param>
    public LoraDatasetHelperViewModel(
        IAppSettingsService settingsService,
        IDatasetEventAggregator eventAggregator,
        IDatasetState state,
        IVideoThumbnailService? videoThumbnailService = null)
    {
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
        _state = state ?? throw new ArgumentNullException(nameof(state));

        // Create tab ViewModels
        DatasetManagement = new DatasetManagementViewModel(settingsService, eventAggregator, state, videoThumbnailService);
        ImageEdit = new ImageEditTabViewModel(eventAggregator, state);
        AutoScaleCrop = new AutoScaleCropTabViewModel();

        // Subscribe to state changes for property forwarding
        _state.StateChanged += OnStateChanged;

        // Subscribe to navigation events to switch tabs
        _eventAggregator.NavigateToImageEditorRequested += OnNavigateToImageEditor;
    }

    /// <summary>
    /// Design-time constructor.
    /// </summary>
    public LoraDatasetHelperViewModel() : this(null!, null!, null!, null)
    {
    }

    #endregion

    #region Event Handlers

    private void OnStateChanged(object? sender, DatasetStateChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IDatasetState.StatusMessage):
                OnPropertyChanged(nameof(StatusMessage));
                break;
            case nameof(IDatasetState.SelectedTabIndex):
                if (_selectedTabIndex != _state.SelectedTabIndex)
                {
                    _selectedTabIndex = _state.SelectedTabIndex;
                    OnPropertyChanged(nameof(SelectedTabIndex));
                }
                break;
        }
    }

    private void OnNavigateToImageEditor(object? sender, NavigateToImageEditorEventArgs e)
    {
        // Switch to Image Edit tab (index 1)
        SelectedTabIndex = 1;
    }

    #endregion

    #region DialogService Forwarding

    /// <summary>
    /// Called when the DialogService is set. Forwards it to child ViewModels.
    /// </summary>
    public void OnDialogServiceSet()
    {
        if (DialogService is not null)
        {
            DatasetManagement.DialogService = DialogService;
            ImageEdit.DialogService = DialogService;
            AutoScaleCrop.DialogService = DialogService;
        }
    }

    #endregion

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
            _state.StateChanged -= OnStateChanged;
            _eventAggregator.NavigateToImageEditorRequested -= OnNavigateToImageEditor;

            // Dispose child ViewModels
            DatasetManagement.Dispose();
            ImageEdit.Dispose();
            AutoScaleCrop.Dispose();
        }

        _disposed = true;
    }

    #endregion
}
