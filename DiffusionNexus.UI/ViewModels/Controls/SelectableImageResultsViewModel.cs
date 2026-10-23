using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DiffusionNexus.UI.ViewModels.Controls;

/// <summary>
/// Backs <see cref="Views.Controls.SelectableImageResultsView"/>: a multi-selectable strip of result
/// tiles (Ctrl-click toggles, Shift-click ranges) layered on top of the existing status tiles, plus a
/// single <see cref="PrimaryItem"/> (the last-clicked tile) that a host can wire to a before/after
/// comparison. The selected set feeds clipboard copy / drag-out (the view) and the reusable
/// <see cref="ImageActionsViewModel"/> "Add Selected To… / Send Selected To…" destinations.
/// </summary>
public partial class SelectableImageResultsViewModel : ObservableObject
{
    private ImageStatusItemViewModel? _lastClickedItem;
    private readonly bool _clearOnNewRun;

    /// <summary>The result tiles (owned by the host; this VM only reads + flips <c>IsSelected</c>).</summary>
    public ObservableCollection<ImageStatusItemViewModel> Items { get; }

    /// <summary>
    /// Upper bound on retained tiles when <c>clearOnNewRun</c> is false, enforced by
    /// <see cref="BeginRun"/> (oldest trimmed first). Each tile pins a decoded thumbnail, so an
    /// uncapped history grows unbounded over a long session. Zero or less means no limit.
    /// </summary>
    public int MaxHistoryItems { get; set; } = 200;

    /// <summary>The reusable Add/Send actions, gated on the current selection.</summary>
    public ImageActionsViewModel Actions { get; }

    /// <summary>The single tile that drives a before/after comparison (last clicked). Two-way.</summary>
    [ObservableProperty] private ImageStatusItemViewModel? _primaryItem;

    /// <summary>Number of currently selected tiles.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SelectionText))]
    private int _selectionCount;

    public bool HasSelection => SelectionCount > 0;

    public string SelectionText => SelectionCount == 1 ? "1 selected" : $"{SelectionCount} selected";

    /// <param name="clearOnNewRun">
    /// Whether <see cref="BeginRun"/> wipes the strip. True (the default) suits hosts that show only
    /// the batch in flight; false keeps a run history that accumulates across runs, capped by
    /// <see cref="MaxHistoryItems"/>.
    /// </param>
    public SelectableImageResultsViewModel(
        ObservableCollection<ImageStatusItemViewModel> items,
        ImageActionsViewModel actions,
        bool clearOnNewRun = true)
    {
        Items = items ?? throw new ArgumentNullException(nameof(items));
        Actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _clearOnNewRun = clearOnNewRun;

        Actions.PathProvider = () => Task.FromResult(new ImageActionPaths(GetSelectedFilePaths()));

        // The host mutates Items directly (e.g. clears it before a new run); keep the selection
        // state consistent when that happens.
        Items.CollectionChanged += OnItemsCollectionChanged;
        UpdateSelectionState();
    }

    /// <summary>
    /// Prepares the strip for a run the host is about to start, then leaves it to append its tiles.
    /// Wipes everything when the host asked to clear; otherwise keeps prior tiles as history and only
    /// drops the carried-over selection and <see cref="PrimaryItem"/> — stale tiles must not stay
    /// armed for Add/Send, and the comparison should follow the new run.
    /// </summary>
    public void BeginRun()
    {
        if (_clearOnNewRun)
        {
            var stale = Items.ToList();
            Items.Clear();
            DisposeThumbnails(stale);
            return;
        }

        TrimToCap();
        ClearSelectionSilent();
        _lastClickedItem = null;
        PrimaryItem = null;
        UpdateSelectionState();
    }

    /// <summary>Drops the oldest tiles until the history fits <see cref="MaxHistoryItems"/>.</summary>
    private void TrimToCap()
    {
        if (MaxHistoryItems <= 0) return;

        var excess = Items.Count - MaxHistoryItems;
        if (excess <= 0) return;

        var dropped = Items.Take(excess).ToList();
        for (var i = 0; i < excess; i++)
            Items.RemoveAt(0);
        DisposeThumbnails(dropped);
    }

    /// <summary>
    /// Releases the decoded thumbnails of tiles already detached from <see cref="Items"/>. Detach
    /// first, dispose second — disposing a bitmap still bound into the visual tree faults the render.
    /// </summary>
    private static void DisposeThumbnails(IEnumerable<ImageStatusItemViewModel> items)
    {
        foreach (var item in items)
        {
            var thumbnail = item.Thumbnail;
            item.Thumbnail = null;
            thumbnail?.Dispose();
        }
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Keep the focus/comparison state consistent regardless of how a host mutates Items: a full
        // reset drops the primary; a removal drops it only if the removed tile was the primary.
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _lastClickedItem = null;
            PrimaryItem = null;
        }
        else if (e.OldItems is not null)
        {
            _lastClickedItem = null;
            if (PrimaryItem is not null && !Items.Contains(PrimaryItem))
                PrimaryItem = null;
        }

        UpdateSelectionState();
    }

    /// <summary>
    /// Applies a click with keyboard modifiers: Shift extends a range from the last click, Ctrl
    /// toggles the clicked tile, and a plain click selects only it. The clicked tile always becomes
    /// the <see cref="PrimaryItem"/> so the comparison follows the user's focus.
    /// </summary>
    public void SelectWithModifiers(ImageStatusItemViewModel? item, bool isShiftPressed, bool isCtrlPressed)
    {
        if (item is null) return;

        if (isShiftPressed && _lastClickedItem is not null)
        {
            SelectRange(_lastClickedItem, item);
        }
        else if (isCtrlPressed)
        {
            item.IsSelected = !item.IsSelected;
        }
        else
        {
            ClearSelectionSilent();
            item.IsSelected = true;
        }

        _lastClickedItem = item;
        PrimaryItem = item;
        UpdateSelectionState();
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in Items)
            item.IsSelected = true;
        UpdateSelectionState();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        ClearSelectionSilent();
        UpdateSelectionState();
    }

    /// <summary>
    /// File paths of the selected tiles for clipboard / drag-out / Add / Send. Each tile contributes
    /// its produced result when available (else its input), de-duplicated and filtered to files that
    /// still exist on disk.
    /// </summary>
    public IReadOnlyList<string> GetSelectedFilePaths()
    {
        return Items
            .Where(item => item.IsSelected)
            .Select(item => item.EffectiveFilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void SelectRange(ImageStatusItemViewModel from, ImageStatusItemViewModel to)
    {
        var fromIndex = Items.IndexOf(from);
        var toIndex = Items.IndexOf(to);
        if (fromIndex == -1 || toIndex == -1) return;

        var start = Math.Min(fromIndex, toIndex);
        var end = Math.Max(fromIndex, toIndex);
        for (var i = start; i <= end; i++)
            Items[i].IsSelected = true;
    }

    private void ClearSelectionSilent()
    {
        foreach (var item in Items)
            item.IsSelected = false;
    }

    private void UpdateSelectionState()
    {
        SelectionCount = Items.Count(item => item.IsSelected);
        Actions.CanAct = HasSelection;
    }
}
