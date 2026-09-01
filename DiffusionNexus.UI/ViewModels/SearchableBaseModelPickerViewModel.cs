using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.Utilities;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// One row offered by the searchable base-model picker: the raw label plus whether
/// it is the currently selected value (drives the selected-row highlight).
/// </summary>
public sealed partial class SearchableBaseModelPickerItem : ObservableObject
{
    public SearchableBaseModelPickerItem(string label, bool isSelected)
    {
        Label = label;
        _isSelected = isSelected;
    }

    public string Label { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// Per-instance engine behind the searchable base-model picker control. Single-select
/// over a list of raw base-model labels, narrowed by a search box using the same rule
/// as the viewer/browser filter flyouts (case-insensitive substring).
/// </summary>
public partial class SearchableBaseModelPickerViewModel : ObservableObject
{
    /// <summary>Default button caption while nothing is selected — single source for
    /// both this VM's initial value and the control's styled-property default.</summary>
    public const string DefaultPlaceholder = "Select base model…";

    /// <summary>Narrows <see cref="VisibleItems"/> by case-insensitive substring.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>The currently selected label, or <c>null</c> when nothing is picked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    private string? _selectedItem;

    /// <summary>Shown on the picker button while nothing is selected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    private string _placeholderText = DefaultPlaceholder;

    /// <summary>
    /// True only when an active (non-whitespace) search matches nothing. Stays false
    /// while the source is merely empty (catalog still loading, or no source at all).
    /// </summary>
    [ObservableProperty]
    private bool _hasNoMatches;

    /// <summary>What the picker button shows: the selection, or the placeholder.</summary>
    public string DisplayText => string.IsNullOrWhiteSpace(SelectedItem) ? PlaceholderText : SelectedItem;

    /// <summary>Raised when a pick was made and the hosting flyout should close.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Called by the control when its flyout opens; a fresh open starts unfiltered.</summary>
    public void OnFlyoutOpened() => SearchText = string.Empty;

    /// <summary>
    /// Commits the single visible item, if the search has narrowed the list to exactly
    /// one — Enter in the search box. Returns whether a pick was made.
    /// </summary>
    public bool TryCommitSingleMatch()
    {
        if (VisibleItems.Count != 1) return false;

        Select(VisibleItems[0].Label);
        return true;
    }

    /// <summary>Picks a label from the list and asks the flyout to close.</summary>
    [RelayCommand]
    private void Select(string label)
    {
        SelectedItem = label;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private IEnumerable<string>? _itemsSource;

    /// <summary>
    /// The full label list the picker offers. An <see cref="INotifyCollectionChanged"/>
    /// source is tracked live — the real sources are observable collections the owning
    /// view models refill when the Civitai catalog resolves.
    /// </summary>
    public IEnumerable<string>? ItemsSource
    {
        get => _itemsSource;
        set
        {
            if (ReferenceEquals(_itemsSource, value)) return;

            if (_itemsSource is INotifyCollectionChanged oldObservable)
            {
                oldObservable.CollectionChanged -= OnItemsSourceCollectionChanged;
            }

            _itemsSource = value;

            if (_itemsSource is INotifyCollectionChanged newObservable)
            {
                newObservable.CollectionChanged += OnItemsSourceCollectionChanged;
            }

            RebuildVisibleItems();
        }
    }

    private void OnItemsSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RebuildVisibleItems();

    /// <summary>The rows currently visible under the active search text.</summary>
    public BatchObservableCollection<SearchableBaseModelPickerItem> VisibleItems { get; } = [];

    partial void OnSearchTextChanged(string value) => RebuildVisibleItems();

    partial void OnSelectedItemChanged(string? value)
    {
        // Flags move in place — no collection event, the realized rows update themselves.
        foreach (var item in VisibleItems)
        {
            item.IsSelected = IsSelectedLabel(item.Label);
        }
    }

    private bool IsSelectedLabel(string label)
        => string.Equals(label, SelectedItem, StringComparison.Ordinal);

    private void RebuildVisibleItems()
    {
        var search = SearchText.Trim();
        var visible = new List<SearchableBaseModelPickerItem>();

        if (_itemsSource is not null)
        {
            foreach (var label in _itemsSource)
            {
                if (search.Length == 0 || label.Contains(search, StringComparison.OrdinalIgnoreCase))
                {
                    visible.Add(new SearchableBaseModelPickerItem(label, IsSelectedLabel(label)));
                }
            }
        }

        // One Reset per rebuild — the owning VMs can refill their source many times
        // per catalog refresh, and each refill must stay cheap on the realized list.
        VisibleItems.ReplaceAll(visible);
        HasNoMatches = visible.Count == 0 && search.Length > 0;
    }
}
