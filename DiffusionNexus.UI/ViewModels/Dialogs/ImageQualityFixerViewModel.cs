using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Service.Services.DatasetQuality;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels.Tabs;
using Serilog;

namespace DiffusionNexus.UI.ViewModels.Dialogs;

/// <summary>
/// Filter chip values for the Image Quality Fixer dialog.
/// </summary>
public enum ImageQualityRatingFilter
{
    /// <summary>Show every row regardless of rating.</summary>
    All,
    /// <summary>Show only images currently rated <see cref="ImageRatingStatus.Unrated"/>.</summary>
    Unrated,
    /// <summary>Show only images currently rated <see cref="ImageRatingStatus.Approved"/> ("Ready").</summary>
    Approved,
    /// <summary>Show only images currently rated <see cref="ImageRatingStatus.Rejected"/> ("Trash").</summary>
    Trash
}

/// <summary>
/// Sort options for the fixer's image grid.
/// </summary>
public enum ImageQualitySortMode
{
    /// <summary>Worst overall score first.</summary>
    OverallScoreAsc,
    /// <summary>Best overall score first.</summary>
    OverallScoreDesc,
    /// <summary>File name A→Z.</summary>
    FileName
}

/// <summary>
/// Window-level VM for the Image Quality Fixer dialog. Owns the rows, selection, sort,
/// rating filter chips, and all commands. Pure presentation logic — IO is delegated to
/// <see cref="DatasetImageViewModel"/> (rating sidecar) and to the host callbacks
/// (<c>RequestReplace</c>, <c>RequestEditInImageEditor</c>).
/// </summary>
public partial class ImageQualityFixerViewModel : ObservableObject
{
    private static readonly ILogger Logger = Log.ForContext<ImageQualityFixerViewModel>();

    private readonly List<ImageQualityFixerItemViewModel> _allItems = [];
    private ImageQualityFixerItemViewModel? _selectedItem;
    private bool _suppressSelectionReset;
    private ImageQualitySortMode _sortMode = ImageQualitySortMode.OverallScoreAsc;
    private ImageQualityRatingFilter _ratingFilter = ImageQualityRatingFilter.All;

    /// <summary>Rows currently visible in the grid (filtered + sorted).</summary>
    public ObservableCollection<ImageQualityFixerItemViewModel> Items { get; } = [];

    /// <summary>Currently focused row in the grid.</summary>
    public ImageQualityFixerItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            // Ignore the transient null the ListBox writes back when we Clear+repopulate Items.
            if (_suppressSelectionReset && value is null)
                return;

            if (SetProperty(ref _selectedItem, value))
            {
                OnPropertyChanged(nameof(HasSelectedItem));
                MarkApprovedCommand.NotifyCanExecuteChanged();
                MarkTrashCommand.NotifyCanExecuteChanged();
                ClearRatingCommand.NotifyCanExecuteChanged();
                ReplaceCommand.NotifyCanExecuteChanged();
                EditInImageEditorCommand.NotifyCanExecuteChanged();
                OpenInExplorerCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>True when the user has a row selected.</summary>
    public bool HasSelectedItem => _selectedItem is not null;

    /// <summary>Current sort mode for the grid.</summary>
    public ImageQualitySortMode SortMode
    {
        get => _sortMode;
        set
        {
            if (SetProperty(ref _sortMode, value))
                RebuildVisibleItems();
        }
    }

    /// <summary>Current rating filter chip.</summary>
    public ImageQualityRatingFilter RatingFilter
    {
        get => _ratingFilter;
        set
        {
            if (SetProperty(ref _ratingFilter, value))
            {
                OnPropertyChanged(nameof(IsAllSelected));
                OnPropertyChanged(nameof(IsUnratedSelected));
                OnPropertyChanged(nameof(IsApprovedSelected));
                OnPropertyChanged(nameof(IsTrashSelected));
                RebuildVisibleItems();
            }
        }
    }

    /// <summary>Bound to the "All" chip's IsChecked.</summary>
    public bool IsAllSelected => RatingFilter == ImageQualityRatingFilter.All;
    /// <summary>Bound to the "Unrated" chip's IsChecked.</summary>
    public bool IsUnratedSelected => RatingFilter == ImageQualityRatingFilter.Unrated;
    /// <summary>Bound to the "Ready" chip's IsChecked.</summary>
    public bool IsApprovedSelected => RatingFilter == ImageQualityRatingFilter.Approved;
    /// <summary>Bound to the "Trash" chip's IsChecked.</summary>
    public bool IsTrashSelected => RatingFilter == ImageQualityRatingFilter.Trash;

    /// <summary>Total number of rows loaded.</summary>
    public int TotalCount => _allItems.Count;

    /// <summary>Number of rows currently rated Unrated.</summary>
    public int UnratedCount => _allItems.Count(i => i.Rating == ImageRatingStatus.Unrated);

    /// <summary>Number of rows currently rated Approved (Ready).</summary>
    public int ApprovedCount => _allItems.Count(i => i.Rating == ImageRatingStatus.Approved);

    /// <summary>Number of rows currently rated Rejected (Trash).</summary>
    public int TrashCount => _allItems.Count(i => i.Rating == ImageRatingStatus.Rejected);

    /// <summary>Number of rows that are checked in the multi-select column.</summary>
    public int SelectedRowCount => _allItems.Count(i => i.IsSelected);

    /// <summary>Summary line shown in the dialog footer.</summary>
    public string SummaryText =>
        $"{TotalCount} images \u00b7 {ApprovedCount} ready \u00b7 {TrashCount} trash \u00b7 {UnratedCount} unrated";

    /// <summary>Marks the selected row as Approved (Ready).</summary>
    public IRelayCommand MarkApprovedCommand { get; }

    /// <summary>Marks the selected row as Rejected (Trash).</summary>
    public IRelayCommand MarkTrashCommand { get; }

    /// <summary>Clears the selected row's rating (back to Unrated).</summary>
    public IRelayCommand ClearRatingCommand { get; }

    /// <summary>Marks every checked (multi-select) row as Trash.</summary>
    public IAsyncRelayCommand BulkMarkTrashCommand { get; }

    /// <summary>Opens the Replace Image dialog for the selected row, then re-runs analysis.</summary>
    public IAsyncRelayCommand ReplaceCommand { get; }

    /// <summary>Sends the selected row to the Image Editor and closes the fixer.</summary>
    public IRelayCommand EditInImageEditorCommand { get; }

    /// <summary>Reveals the selected row's file in OS file explorer.</summary>
    public IRelayCommand OpenInExplorerCommand { get; }

    /// <summary>
    /// Optional dialog service for confirmations. Wired by the host before showing.
    /// </summary>
    public IDialogService? DialogService { get; set; }

    /// <summary>
    /// Invoked when the user clicks "Replace" on a row that has a resolved
    /// <see cref="DatasetImageViewModel"/>. Implementer must show the replace
    /// dialog, perform the file swap, and re-run image-quality analysis on the
    /// returned <see cref="PerImageQualitySummary"/> (or null if the user
    /// cancelled). The fixer updates the row in place when a non-null summary
    /// is returned.
    /// </summary>
    public Func<ImageQualityFixerItemViewModel, Task<PerImageQualitySummary?>>? RequestReplace { get; set; }

    /// <summary>
    /// Invoked when the user clicks "Edit in Image Editor" on a row. Implementer
    /// is expected to publish the navigation event and close the fixer dialog.
    /// </summary>
    public Action<ImageQualityFixerItemViewModel>? RequestEditInImageEditor { get; set; }

    /// <summary>
    /// Invoked when the user wants to reveal the selected file in the OS file
    /// browser. Defaults to a no-op when not wired.
    /// </summary>
    public Action<string>? RequestOpenInExplorer { get; set; }

    /// <summary>
    /// Creates a new fixer VM. Use <see cref="LoadItems"/> to populate after construction.
    /// </summary>
    public ImageQualityFixerViewModel()
    {
        MarkApprovedCommand = new RelayCommand(MarkApproved, () => SelectedItem?.CanMutateRating == true && SelectedItem.Rating != ImageRatingStatus.Approved);
        MarkTrashCommand = new RelayCommand(MarkTrash, () => SelectedItem?.CanMutateRating == true && SelectedItem.Rating != ImageRatingStatus.Rejected);
        ClearRatingCommand = new RelayCommand(ClearRating, () => SelectedItem?.CanMutateRating == true && SelectedItem.Rating != ImageRatingStatus.Unrated);
        BulkMarkTrashCommand = new AsyncRelayCommand(BulkMarkTrashAsync, () => _allItems.Any(i => i.IsSelected && i.CanMutateRating));
        ReplaceCommand = new AsyncRelayCommand(ReplaceAsync, () => SelectedItem?.DatasetImage is not null && RequestReplace is not null);
        EditInImageEditorCommand = new RelayCommand(EditInImageEditor, () => SelectedItem is not null && RequestEditInImageEditor is not null);
        OpenInExplorerCommand = new RelayCommand(OpenInExplorer, () => SelectedItem is not null);
    }

    /// <summary>
    /// Replaces the contents of the dialog. Wires per-row PropertyChanged so chip
    /// counts and bulk-button state stay in sync when the user toggles ratings or
    /// row checkboxes from the grid.
    /// </summary>
    public void LoadItems(IEnumerable<ImageQualityFixerItemViewModel> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        foreach (var existing in _allItems)
            existing.PropertyChanged -= OnItemPropertyChanged;

        _allItems.Clear();
        foreach (var item in items)
        {
            _allItems.Add(item);
            item.PropertyChanged += OnItemPropertyChanged;
        }

        RebuildVisibleItems();
        SelectedItem = Items.FirstOrDefault();
        NotifyAggregatesChanged();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ImageQualityFixerItemViewModel.Rating))
        {
            NotifyAggregatesChanged();
            // Refresh visibility because the row might no longer match the current chip.
            RebuildVisibleItems();
            // The mutated row's rating drives the rating-button enabled state.
            if (ReferenceEquals(sender, SelectedItem))
            {
                MarkApprovedCommand.NotifyCanExecuteChanged();
                MarkTrashCommand.NotifyCanExecuteChanged();
                ClearRatingCommand.NotifyCanExecuteChanged();
            }
        }
        else if (e.PropertyName is nameof(ImageQualityFixerItemViewModel.IsSelected))
        {
            OnPropertyChanged(nameof(SelectedRowCount));
            BulkMarkTrashCommand.NotifyCanExecuteChanged();
        }
    }

    private void NotifyAggregatesChanged()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(UnratedCount));
        OnPropertyChanged(nameof(ApprovedCount));
        OnPropertyChanged(nameof(TrashCount));
        OnPropertyChanged(nameof(SelectedRowCount));
        OnPropertyChanged(nameof(SummaryText));
        BulkMarkTrashCommand.NotifyCanExecuteChanged();
    }

    private void RebuildVisibleItems()
    {
        var filtered = _allItems.Where(MatchesFilter);
        var sorted = SortMode switch
        {
            ImageQualitySortMode.OverallScoreDesc => filtered.OrderByDescending(i => double.IsNaN(i.OverallScore) ? double.MinValue : i.OverallScore),
            ImageQualitySortMode.FileName => filtered.OrderBy(i => i.FileName, StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderBy(i => double.IsNaN(i.OverallScore) ? double.MaxValue : i.OverallScore),
        };

        // Capture selection before mutating Items so the ListBox's transient null write-back
        // (triggered by Items.Clear) doesn't drop the user's selection.
        var previousSelection = _selectedItem;
        _suppressSelectionReset = true;
        try
        {
            Items.Clear();
            foreach (var item in sorted)
                Items.Add(item);
        }
        finally
        {
            _suppressSelectionReset = false;
        }

        // Preserve selection where possible.
        SelectedItem = previousSelection is not null && Items.Contains(previousSelection)
            ? previousSelection
            : Items.FirstOrDefault();
    }

    private bool MatchesFilter(ImageQualityFixerItemViewModel item) => RatingFilter switch
    {
        ImageQualityRatingFilter.Unrated => item.Rating == ImageRatingStatus.Unrated,
        ImageQualityRatingFilter.Approved => item.Rating == ImageRatingStatus.Approved,
        ImageQualityRatingFilter.Trash => item.Rating == ImageRatingStatus.Rejected,
        _ => true
    };

    private void MarkApproved() => SetRating(SelectedItem, ImageRatingStatus.Approved);

    private void MarkTrash() => SetRating(SelectedItem, ImageRatingStatus.Rejected);

    private void ClearRating() => SetRating(SelectedItem, ImageRatingStatus.Unrated);

    private static void SetRating(ImageQualityFixerItemViewModel? item, ImageRatingStatus rating)
    {
        if (item?.DatasetImage is null)
            return;

        // DatasetImageViewModel exposes MarkApproved/MarkRejected/ClearRating commands
        // that handle persistence + event aggregator publish. Use the simplest path
        // by toggling RatingStatus + SaveRating directly to keep this VM independent.
        item.DatasetImage.RatingStatus = rating;
        try
        {
            item.DatasetImage.SaveRating();
        }
        catch (IOException ex)
        {
            Logger.Warning(ex, "Failed to persist rating for {FilePath}", item.FilePath);
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger.Warning(ex, "No permission to persist rating for {FilePath}", item.FilePath);
        }

        item.Rating = rating;
    }

    private async Task BulkMarkTrashAsync()
    {
        var targets = _allItems.Where(i => i.IsSelected && i.CanMutateRating).ToList();
        if (targets.Count == 0)
            return;

        if (DialogService is not null)
        {
            var confirmed = await DialogService.ShowConfirmAsync(
                "Mark as Trash",
                $"Mark {targets.Count} image{(targets.Count == 1 ? string.Empty : "s")} as Trash? You can undo this from the per-image controls.");
            if (!confirmed)
                return;
        }

        foreach (var target in targets)
        {
            SetRating(target, ImageRatingStatus.Rejected);
            target.IsSelected = false;
        }
    }

    private async Task ReplaceAsync()
    {
        if (SelectedItem is null || RequestReplace is null)
            return;

        var refreshed = await RequestReplace(SelectedItem);
        if (refreshed is null)
            return;

        // Replace the row's underlying summary by swapping in a fresh item that wraps the new scores.
        var index = _allItems.IndexOf(SelectedItem);
        if (index < 0)
            return;

        var advice = ImageQualityAdvisor.Analyze(refreshed);
        var replacement = new ImageQualityFixerItemViewModel(
            refreshed,
            advice,
            SelectedItem.DatasetImage,
            SelectedItem.Width,
            SelectedItem.Height,
            SelectedItem.FileSizeBytes);

        SelectedItem.PropertyChanged -= OnItemPropertyChanged;
        _allItems[index] = replacement;
        replacement.PropertyChanged += OnItemPropertyChanged;

        RebuildVisibleItems();
        SelectedItem = replacement;
        NotifyAggregatesChanged();
    }

    private void EditInImageEditor()
    {
        if (SelectedItem is null || RequestEditInImageEditor is null)
            return;

        RequestEditInImageEditor(SelectedItem);
    }

    private void OpenInExplorer()
    {
        if (SelectedItem is null)
            return;

        if (RequestOpenInExplorer is not null)
        {
            RequestOpenInExplorer(SelectedItem.FilePath);
            return;
        }

        // No host handler — silently no-op. Tests don't need real shell integration.
    }
}
