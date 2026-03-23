using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Controls;

/// <summary>
/// Reusable dataset and version selector control.
/// Provides the standard searchable dataset dropdown and version dropdown
/// with "V1 | 45 Images" display format used throughout the application.
/// </summary>
public partial class DatasetVersionSelectorControl : UserControl
{
    /// <summary>
    /// Defines the <see cref="Datasets"/> property.
    /// </summary>
    public static readonly StyledProperty<IEnumerable?> DatasetsProperty =
        AvaloniaProperty.Register<DatasetVersionSelectorControl, IEnumerable?>(nameof(Datasets));

    /// <summary>
    /// Defines the <see cref="SelectedDataset"/> property.
    /// </summary>
    public static readonly StyledProperty<DatasetCardViewModel?> SelectedDatasetProperty =
        AvaloniaProperty.Register<DatasetVersionSelectorControl, DatasetCardViewModel?>(
            nameof(SelectedDataset),
            defaultBindingMode: BindingMode.TwoWay);

    /// <summary>
    /// Defines the <see cref="VersionItems"/> property.
    /// </summary>
    public static readonly StyledProperty<IEnumerable?> VersionItemsProperty =
        AvaloniaProperty.Register<DatasetVersionSelectorControl, IEnumerable?>(nameof(VersionItems));

    /// <summary>
    /// Defines the <see cref="SelectedVersion"/> property.
    /// </summary>
    public static readonly StyledProperty<EditorVersionItem?> SelectedVersionProperty =
        AvaloniaProperty.Register<DatasetVersionSelectorControl, EditorVersionItem?>(
            nameof(SelectedVersion),
            defaultBindingMode: BindingMode.TwoWay);

    /// <summary>
    /// Defines the <see cref="IsDatasetSearchable"/> property.
    /// </summary>
    public static readonly StyledProperty<bool> IsDatasetSearchableProperty =
        AvaloniaProperty.Register<DatasetVersionSelectorControl, bool>(nameof(IsDatasetSearchable), true);

    /// <summary>
    /// Defines the <see cref="DatasetPlaceholderText"/> property.
    /// </summary>
    public static readonly StyledProperty<string> DatasetPlaceholderTextProperty =
        AvaloniaProperty.Register<DatasetVersionSelectorControl, string>(
            nameof(DatasetPlaceholderText), "Search datasets...");

    /// <summary>
    /// Defines the <see cref="AutoSelectLastVersion"/> property.
    /// When true, the control automatically selects the last version item
    /// whenever the <see cref="VersionItems"/> collection changes.
    /// Default is true.
    /// </summary>
    public static readonly StyledProperty<bool> AutoSelectLastVersionProperty =
        AvaloniaProperty.Register<DatasetVersionSelectorControl, bool>(nameof(AutoSelectLastVersion), true);

    /// <summary>
    /// Gets or sets the collection of datasets to display.
    /// </summary>
    public IEnumerable? Datasets
    {
        get => GetValue(DatasetsProperty);
        set => SetValue(DatasetsProperty, value);
    }

    /// <summary>
    /// Gets or sets the currently selected dataset.
    /// </summary>
    public DatasetCardViewModel? SelectedDataset
    {
        get => GetValue(SelectedDatasetProperty);
        set => SetValue(SelectedDatasetProperty, value);
    }

    /// <summary>
    /// Gets or sets the collection of version items to display.
    /// </summary>
    public IEnumerable? VersionItems
    {
        get => GetValue(VersionItemsProperty);
        set => SetValue(VersionItemsProperty, value);
    }

    /// <summary>
    /// Gets or sets the currently selected version.
    /// </summary>
    public EditorVersionItem? SelectedVersion
    {
        get => GetValue(SelectedVersionProperty);
        set => SetValue(SelectedVersionProperty, value);
    }

    /// <summary>
    /// Gets or sets whether the dataset combo box supports text search (IsEditable).
    /// Default is true.
    /// </summary>
    public bool IsDatasetSearchable
    {
        get => GetValue(IsDatasetSearchableProperty);
        set => SetValue(IsDatasetSearchableProperty, value);
    }

    /// <summary>
    /// Gets or sets the placeholder text for the dataset combo box.
    /// Default is "Search datasets...".
    /// </summary>
    public string DatasetPlaceholderText
    {
        get => GetValue(DatasetPlaceholderTextProperty);
        set => SetValue(DatasetPlaceholderTextProperty, value);
    }

    /// <summary>
    /// Gets or sets whether the control automatically selects the last version item
    /// when the <see cref="VersionItems"/> collection changes.
    /// Default is true.
    /// </summary>
    public bool AutoSelectLastVersion
    {
        get => GetValue(AutoSelectLastVersionProperty);
        set => SetValue(AutoSelectLastVersionProperty, value);
    }

    private bool _autoSelectScheduled;

    // Tracks the last non-null selections so they can be restored after a visual-tree
    // detach/reattach cycle (editable ComboBoxes clear their selection on detach).
    private DatasetCardViewModel? _lastValidDataset;
    private EditorVersionItem? _lastValidVersion;
    private bool _wasDetached;

    public DatasetVersionSelectorControl()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Track last valid (non-null) selections for restore-on-reattach
        if (change.Property == SelectedDatasetProperty && change.NewValue is DatasetCardViewModel ds)
        {
            _lastValidDataset = ds;
        }
        else if (change.Property == SelectedVersionProperty && change.NewValue is EditorVersionItem ver)
        {
            _lastValidVersion = ver;
        }

        if (change.Property == VersionItemsProperty)
        {
            // Unsubscribe from old collection
            if (change.OldValue is INotifyCollectionChanged oldCollection)
            {
                oldCollection.CollectionChanged -= OnVersionItemsCollectionChanged;
            }

            // Subscribe to new collection
            if (change.NewValue is INotifyCollectionChanged newCollection)
            {
                newCollection.CollectionChanged += OnVersionItemsCollectionChanged;
            }
        }
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _wasDetached = true;
        base.OnDetachedFromVisualTree(e);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (_wasDetached)
        {
            _wasDetached = false;
            RestoreSelectionsIfNeeded();
        }
    }

    /// <summary>
    /// Restores dataset and version selections that were lost when editable ComboBoxes
    /// cleared their SelectedItem during a visual-tree detach/reattach cycle.
    /// </summary>
    private void RestoreSelectionsIfNeeded()
    {
        var dataset = _lastValidDataset;
        var version = _lastValidVersion;

        if (dataset is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            // Temporarily disable auto-select so the version restore is not overridden
            var wasAutoSelect = AutoSelectLastVersion;
            AutoSelectLastVersion = false;

            try
            {
                if (SelectedDataset is null)
                {
                    SelectedDataset = dataset;
                }

                if (version is not null && VersionItems is not null)
                {
                    foreach (var item in VersionItems)
                    {
                        if (item is EditorVersionItem evi && evi.Version == version.Version)
                        {
                            SelectedVersion = evi;
                            break;
                        }
                    }
                }
            }
            finally
            {
                AutoSelectLastVersion = wasAutoSelect;
            }
        }, DispatcherPriority.Background);
    }

    private void OnVersionItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!AutoSelectLastVersion || _autoSelectScheduled)
        {
            return;
        }

        // Defer selection until all synchronous collection changes have been processed
        _autoSelectScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _autoSelectScheduled = false;
            SelectLastVersionItem();
        }, DispatcherPriority.Background);
    }

    private void SelectLastVersionItem()
    {
        if (!AutoSelectLastVersion)
        {
            return;
        }

        var items = VersionItems;
        if (items is null)
        {
            return;
        }

        EditorVersionItem? last = null;
        foreach (var item in items)
        {
            if (item is EditorVersionItem evi)
            {
                last = evi;
            }
        }

        if (last is not null)
        {
            SelectedVersion = last;
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
