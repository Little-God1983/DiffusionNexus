using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Utilities;

namespace DiffusionNexus.UI.ViewModels;

public enum ViewerSortOption
{
    Name,
    CreationDate
}

public sealed record ViewerSortOptionItem(ViewerSortOption Option, string DisplayName);

public partial class ViewerMediaItemViewModel : ObservableObject
{
    public ViewerMediaItemViewModel(string filePath, bool isVideo, DateTime createdAt, string formatTag)
    {
        FilePath = filePath;
        IsVideo = isVideo;
        CreatedAt = createdAt;
        FormatTag = formatTag;
    }

    public string FilePath { get; }

    public string FileName => Path.GetFileName(FilePath);

    public bool IsVideo { get; }

    public bool IsImage => !IsVideo;

    public DateTime CreatedAt { get; }

    public string FormatTag { get; }
}

public partial class ViewerViewModel : ViewModelBase
{
    private readonly IAppSettingsService _settingsService;
    private readonly IDatasetEventAggregator? _eventAggregator;
    private readonly List<ViewerMediaItemViewModel> _allItems = [];

    public ObservableCollection<ViewerMediaItemViewModel> MediaItems { get; } = [];

    public IReadOnlyList<ViewerSortOptionItem> SortOptions { get; } =
    [
        new(ViewerSortOption.Name, "Name"),
        new(ViewerSortOption.CreationDate, "Creation Date")
    ];

    [ObservableProperty]
    private ViewerSortOptionItem? _selectedSortOption;

    [ObservableProperty]
    private double _tileWidth = 220;

    public bool HasMedia => MediaItems.Count > 0;

    public ViewerViewModel(IAppSettingsService settingsService, IDatasetEventAggregator? eventAggregator = null)
    {
        _settingsService = settingsService;
        _eventAggregator = eventAggregator;

        SelectedSortOption = SortOptions.First();
        MediaItems.CollectionChanged += OnMediaItemsChanged;
    }

    public ViewerViewModel()
    {
        _settingsService = null!;
        _eventAggregator = null;

        SelectedSortOption = SortOptions.First();
        MediaItems.CollectionChanged += OnMediaItemsChanged;
    }

    private void OnMediaItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasMedia));
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_settingsService is null)
            return;

        var settings = await _settingsService.GetSettingsAsync();
        var enabledGalleries = settings.ImageGalleries
            .Where(gallery => gallery.IsEnabled)
            .Select(gallery => gallery.FolderPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var items = await Task.Run(() => LoadMediaItems(enabledGalleries));

        _allItems.Clear();
        _allItems.AddRange(items);

        ApplySorting();
    }

    [RelayCommand]
    private void OpenSettings()
    {
        _eventAggregator?.PublishNavigateToSettings(new NavigateToSettingsEventArgs());
    }

    private static IReadOnlyList<ViewerMediaItemViewModel> LoadMediaItems(IEnumerable<string> galleryPaths)
    {
        var items = new List<ViewerMediaItemViewModel>();

        foreach (var rootPath in galleryPaths)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
                continue;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(rootPath, "*.*", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            try
            {
                foreach (var file in files)
                {
                    if (!MediaFileExtensions.IsDisplayableMediaFile(file))
                        continue;

                    var isVideo = MediaFileExtensions.IsVideoFile(file);
                    var createdAt = File.GetCreationTime(file);
                    var formatTag = isVideo
                        ? SupportedMediaTypes.VideoExtensionsDisplay
                        : SupportedMediaTypes.ImageExtensionsDisplay;

                    items.Add(new ViewerMediaItemViewModel(file, isVideo, createdAt, formatTag));
                }
            }
            catch
            {
                // Skip unreadable paths
            }
        }

        return items;
    }

    partial void OnSelectedSortOptionChanged(ViewerSortOptionItem? value)
    {
        ApplySorting();
    }

    private void ApplySorting()
    {
        var sorted = SelectedSortOption?.Option switch
        {
            ViewerSortOption.CreationDate => _allItems
                .OrderBy(item => item.CreatedAt)
                .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase),
            _ => _allItems
                .OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.CreatedAt)
        };

        MediaItems.Clear();
        foreach (var item in sorted)
        {
            MediaItems.Add(item);
        }
    }
}
