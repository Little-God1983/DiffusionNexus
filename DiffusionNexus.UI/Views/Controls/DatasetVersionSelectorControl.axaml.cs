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

    // Tracks the last non-null selections so they can be restored after any visual-tree
    // attach (first-time or re-attach).  Editable ComboBoxes clear their selection when
    // detached, and item containers may not yet be generated on first attach.
    private DatasetCardViewModel? _lastValidDataset;
    private EditorVersionItem? _lastValidVersion;

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
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Always restore on attach, not just re-attach.
        // On first attach the dataset may have been set in the VM before the tab became
        // visible.  Avalonia's ComboBox can miss the SelectedItem because item containers
        // aren't generated yet when the binding fires, so the restore nudges it after
        // the first layout pass.  On re-attach the editable ComboBox clears its text on
        // detach, so the same logic is needed there too.
        RestoreSelectionsIfNeeded();
    }

    /// <summary>
    /// Restores dataset and version selections after a visual-tree attach (first-time or
    /// re-attach).  Uses a double Background post: the outer post is enqueued immediately;
    /// Avalonia's binding re-activation (which may also run at Background priority) is
    /// enqueued around the same time; the inner post is enqueued only after the outer post
    /// runs, guaranteeing it executes after any same-priority binding work has completed and
    /// <see cref="_lastValidDataset"/> has been populated.
    /// The restore sets the inner ComboBox's SelectedItem directly to avoid triggering the
    /// TwoWay-binding chain back into the VM (which would clear and repopulate versions).
    /// </summary>
    private void RestoreSelectionsIfNeeded()
    {
        Dispatcher.UIThread.Post(() =>
        {
            Dispatcher.UIThread.Post(ApplyStoredSelections, DispatcherPriority.Background);
        }, DispatcherPriority.Background);
    }

    private void ApplyStoredSelections()
    {
        var dataset = _lastValidDataset;
        if (dataset is null)
        {
            return;
        }

        var datasetCombo = this.FindControl<ComboBox>("DatasetComboBox");
        if (datasetCombo is not null && !ReferenceEquals(datasetCombo.SelectedItem, dataset))
        {
            datasetCombo.SelectedItem = dataset;
        }

        var version = _lastValidVersion;
        if (version is null || VersionItems is null)
        {
            return;
        }

        var versionCombo = this.FindControl<ComboBox>("VersionComboBox");
        if (versionCombo is null)
        {
            return;
        }

        foreach (var item in VersionItems)
        {
            if (item is EditorVersionItem evi && evi.Version == version.Version)
            {
                if (!ReferenceEquals(versionCombo.SelectedItem, evi))
                {
                    versionCombo.SelectedItem = evi;
                }
                break;
            }
        }
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
