using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the Generation Gallery mosaic gallery.
/// </summary>
public partial class GenerationGalleryViewModel : BusyViewModelBase
{
    private readonly IAppSettingsService? _settingsService;
    private readonly IDatasetEventAggregator? _eventAggregator;
    private readonly List<GenerationGalleryMediaItemViewModel> _allMediaItems = [];
    private GenerationGalleryMediaItemViewModel? _lastClickedItem;

    public GenerationGalleryViewModel()
    {
        _settingsService = null;
        _eventAggregator = null;
        InitializeCommands();
        LoadDesignData();
    }

    public GenerationGalleryViewModel(IAppSettingsService settingsService, IDatasetEventAggregator eventAggregator)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
        
        _eventAggregator.SettingsSaved += OnSettingsSaved;
        InitializeCommands();
    }

    private void OnSettingsSaved(object? sender, SettingsSavedEventArgs e)
    {
        // Reload gallery when settings change (new folders might be added)
        LoadMediaCommand.Execute(null);
    }

    public ObservableCollection<GenerationGalleryMediaItemViewModel> MediaItems { get; } = [];

    public IReadOnlyList<string> SortOptions { get; } = ["Name", "Creation date"];

    public string ImageExtensionsDisplay => SupportedMediaTypes.ImageExtensionsDisplay;

    public string VideoExtensionsDisplay => SupportedMediaTypes.VideoExtensionsDisplay;

    [ObservableProperty]
    private string _selectedSortOption = "Creation date";

    [ObservableProperty]
    private double _tileWidth = 220;

    [ObservableProperty]
    private string? _noMediaMessage;

    [ObservableProperty]
    private int _selectionCount;

    public bool HasMedia => MediaItems.Count > 0;

    public bool HasNoMedia => !HasMedia;

    public bool HasSelection => SelectionCount > 0;

    public IRelayCommand SelectAllCommand { get; private set; } = null!;

    public IRelayCommand ClearSelectionCommand { get; private set; } = null!;

    public IAsyncRelayCommand DeleteSelectedCommand { get; private set; } = null!;

    public IAsyncRelayCommand<GenerationGalleryMediaItemViewModel?> DeleteMediaCommand { get; private set; } = null!;

    [RelayCommand]
    private async Task LoadMediaAsync()
    {
        if (_settingsService is null)
        {
            LoadDesignData();
            return;
        }

        await RunBusyAsync(async () =>
        {
            var settings = await _settingsService.GetSettingsAsync();
            var enabledPaths = GetEnabledGalleryPaths(settings);

            var mediaItems = await Task.Run(() => CollectMediaItems(enabledPaths));
            await ApplyMediaItemsAsync(mediaItems, enabledPaths.Count);
        }, "Loading gallery...");
    }

    partial void OnSelectedSortOptionChanged(string value)
    {
        ApplySorting();
    }

    private static List<string> GetEnabledGalleryPaths(AppSettings settings)
    {
        return settings.ImageGalleries
            .Where(g => g.IsEnabled && !string.IsNullOrWhiteSpace(g.FolderPath))
            .OrderBy(g => g.Order)
            .Select(g => g.FolderPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<GenerationGalleryMediaItemViewModel> CollectMediaItems(IEnumerable<string> paths)
    {
        var items = new List<GenerationGalleryMediaItemViewModel>();
        foreach (var root in paths)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in EnumerateMediaFiles(root))
            {
                var isVideo = SupportedMediaTypes.IsVideoFile(file);
                var createdAt = File.GetCreationTimeUtc(file);
                items.Add(new GenerationGalleryMediaItemViewModel(file, isVideo, createdAt));
            }
        }

        return items;
    }

    private static IEnumerable<string> EnumerateMediaFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> files = [];
            IEnumerable<string> directories = [];

            try
            {
                files = Directory.EnumerateFiles(current, "*.*", SearchOption.TopDirectoryOnly);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (SupportedMediaTypes.IsMediaFile(file))
                {
                    yield return file;
                }
            }

            try
            {
                directories = Directory.EnumerateDirectories(current);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var directory in directories)
            {
                pending.Push(directory);
            }
        }
    }

    private async Task ApplyMediaItemsAsync(List<GenerationGalleryMediaItemViewModel> items, int enabledSourceCount)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyMediaItems(items, enabledSourceCount);
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() => ApplyMediaItems(items, enabledSourceCount));
    }

    private void ApplyMediaItems(List<GenerationGalleryMediaItemViewModel> items, int enabledSourceCount)
    {
        _allMediaItems.Clear();
        _allMediaItems.AddRange(items);

        ClearSelectionSilent();
        ApplySorting();

        NoMediaMessage = enabledSourceCount == 0
            ? "No generation gallery folders are enabled. Configure Generation Galleries in Settings to get started."
            : "No media found in enabled generation gallery folders. Check your Generation Galleries in Settings.";

        UpdateMediaVisibility();
    }

    private void ApplySorting()
    {
        IEnumerable<GenerationGalleryMediaItemViewModel> sorted = _allMediaItems;

        if (string.Equals(SelectedSortOption, "Creation date", StringComparison.OrdinalIgnoreCase))
        {
            sorted = sorted.OrderByDescending(item => item.CreatedAtUtc)
                .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            sorted = sorted.OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase);
        }

        MediaItems.Clear();
        foreach (var item in sorted)
        {
            MediaItems.Add(item);
        }

        UpdateMediaVisibility();
    }

    private void LoadDesignData()
    {
        _allMediaItems.Clear();
        _allMediaItems.Add(new GenerationGalleryMediaItemViewModel("C:\\Images\\Sample-01.png", false, DateTime.UtcNow.AddDays(-1)));
        _allMediaItems.Add(new GenerationGalleryMediaItemViewModel("C:\\Images\\Sample-02.jpg", false, DateTime.UtcNow));
        _allMediaItems.Add(new GenerationGalleryMediaItemViewModel("C:\\Videos\\Sample-03.mp4", true, DateTime.UtcNow.AddHours(-4)));

        ApplySorting();
    }

    private void InitializeCommands()
    {
        SelectAllCommand = new RelayCommand(SelectAll);
        ClearSelectionCommand = new RelayCommand(ClearSelection);
        DeleteSelectedCommand = new AsyncRelayCommand(DeleteSelectedAsync);
        DeleteMediaCommand = new AsyncRelayCommand<GenerationGalleryMediaItemViewModel?>(DeleteMediaAsync);
    }

    public void SelectWithModifiers(GenerationGalleryMediaItemViewModel? item, bool isShiftPressed, bool isCtrlPressed)
    {
        if (item is null) return;

        if (isShiftPressed && _lastClickedItem is not null)
        {
            SelectRange(_lastClickedItem, item);
        }
        else if (isCtrlPressed)
        {
            item.IsSelected = !item.IsSelected;
            _lastClickedItem = item;
        }
        else
        {
            ClearSelectionSilent();
            item.IsSelected = true;
            _lastClickedItem = item;
        }

        UpdateSelectionCount();
    }

    private void SelectRange(GenerationGalleryMediaItemViewModel from, GenerationGalleryMediaItemViewModel to)
    {
        var fromIndex = MediaItems.IndexOf(from);
        var toIndex = MediaItems.IndexOf(to);

        if (fromIndex == -1 || toIndex == -1) return;

        var startIndex = Math.Min(fromIndex, toIndex);
        var endIndex = Math.Max(fromIndex, toIndex);

        for (var i = startIndex; i <= endIndex; i++)
        {
            MediaItems[i].IsSelected = true;
        }

        _lastClickedItem = to;
    }

    private void SelectAll()
    {
        foreach (var item in MediaItems)
        {
            item.IsSelected = true;
        }

        UpdateSelectionCount();
    }

    private void ClearSelection()
    {
        ClearSelectionSilent();
        UpdateSelectionCount();
    }

    private void ClearSelectionSilent()
    {
        foreach (var item in MediaItems)
        {
            item.IsSelected = false;
        }

        _lastClickedItem = null;
        SelectionCount = 0;
        OnPropertyChanged(nameof(HasSelection));
    }

    private void UpdateSelectionCount()
    {
        SelectionCount = MediaItems.Count(item => item.IsSelected);
        OnPropertyChanged(nameof(HasSelection));
    }

    private async Task DeleteSelectedAsync()
    {
        if (DialogService is null) return;

        var selectedItems = MediaItems.Where(item => item.IsSelected).ToList();
        if (selectedItems.Count == 0) return;

        var confirm = await DialogService.ShowConfirmAsync(
            "Delete Selected Media",
            $"Are you sure you want to delete {selectedItems.Count} selected media items?");

        if (!confirm) return;

        foreach (var item in selectedItems)
        {
            await DeleteMediaInternalAsync(item);
        }

        ClearSelectionSilent();
        UpdateSelectionCount();
        UpdateMediaVisibility();
    }

    private async Task DeleteMediaAsync(GenerationGalleryMediaItemViewModel? item)
    {
        if (item is null || DialogService is null) return;

        var confirm = await DialogService.ShowConfirmAsync(
            "Delete Media",
            $"Delete '{item.FullFileName}'?");

        if (!confirm) return;

        await DeleteMediaInternalAsync(item);
        UpdateSelectionCount();
        UpdateMediaVisibility();
    }

    private async Task DeleteMediaInternalAsync(GenerationGalleryMediaItemViewModel item)
    {
        try
        {
            if (File.Exists(item.FilePath))
            {
                File.Delete(item.FilePath);
            }

            MediaItems.Remove(item);
            _allMediaItems.Remove(item);

            if (_lastClickedItem == item)
            {
                _lastClickedItem = null;
            }
        }
        catch
        {
            if (DialogService is not null)
            {
                await DialogService.ShowMessageAsync("Delete Failed", $"Could not delete '{item.FullFileName}'.");
            }
        }
    }

    private void UpdateMediaVisibility()
    {
        OnPropertyChanged(nameof(HasMedia));
        OnPropertyChanged(nameof(HasNoMedia));
    }
}
