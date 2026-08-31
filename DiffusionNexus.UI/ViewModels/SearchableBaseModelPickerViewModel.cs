using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Per-instance engine behind the searchable base-model picker control. Single-select
/// over a list of raw base-model labels, narrowed by a search box using the same rule
/// as the viewer/browser filter flyouts (case-insensitive substring).
/// </summary>
public partial class SearchableBaseModelPickerViewModel : ObservableObject
{
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
    private string _placeholderText = "Select base model…";

    /// <summary>What the picker button shows: the selection, or the placeholder.</summary>
    public string DisplayText => string.IsNullOrWhiteSpace(SelectedItem) ? PlaceholderText : SelectedItem;

    /// <summary>Raised when a pick was made and the hosting flyout should close.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Called by the control when its flyout opens; a fresh open starts unfiltered.</summary>
    public void OnFlyoutOpened() => SearchText = string.Empty;

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
    /// source is tracked live — the real sources are <c>ObservableCollection&lt;string&gt;</c>
    /// the owning view models Clear() and re-fill when the Civitai catalog resolves.
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

    /// <summary>The labels currently visible under the active search text.</summary>
    public ObservableCollection<string> VisibleItems { get; } = [];

    partial void OnSearchTextChanged(string value) => RebuildVisibleItems();

    private void RebuildVisibleItems()
    {
        VisibleItems.Clear();
        if (_itemsSource is null) return;

        var search = SearchText.Trim();
        foreach (var label in _itemsSource)
        {
            if (search.Length == 0 || label.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                VisibleItems.Add(label);
            }
        }
    }
}
