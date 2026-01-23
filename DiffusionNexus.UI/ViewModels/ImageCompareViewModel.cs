using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.Controls;

namespace DiffusionNexus.UI.ViewModels;

public partial class ImageCompareViewModel : ViewModelBase
{
    private const double TrayOpenHeight = 260;
    private const double TrayClosedHeight = 28;
    private readonly TimeSpan _trayCloseDelay = TimeSpan.FromMilliseconds(300);
    private CancellationTokenSource? _trayCloseCts;

    public ObservableCollection<ImageCompareDataset> AvailableDatasets { get; } = [];
    public ObservableCollection<ImageCompareFitMode> FitModeOptions { get; } = new(Enum.GetValues<ImageCompareFitMode>());
    public ObservableCollection<ImageCompareItem> FilmstripItems { get; } = [];

    [ObservableProperty]
    private ImageCompareDataset? _beforeDataset;

    [ObservableProperty]
    private ImageCompareDataset? _afterDataset;

    [ObservableProperty]
    private ImageCompareItem? _selectedBeforeItem;

    [ObservableProperty]
    private ImageCompareItem? _selectedAfterItem;

    [ObservableProperty]
    private string? _beforeImagePath;

    [ObservableProperty]
    private string? _afterImagePath;

    [ObservableProperty]
    private string? _beforeImageLabel;

    [ObservableProperty]
    private string? _afterImageLabel;

    [ObservableProperty]
    private ImageCompareAssignTarget _assignTarget = ImageCompareAssignTarget.Before;

    [ObservableProperty]
    private ImageCompareFitMode _fitMode = ImageCompareFitMode.Fit;

    [ObservableProperty]
    private double _sliderValue = 0.5;

    [ObservableProperty]
    private bool _isTrayPinned;

    [ObservableProperty]
    private bool _isTrayOpen;

    [ObservableProperty]
    private double _trayHeight = TrayClosedHeight;

    [ObservableProperty]
    private string? _searchText;

    public ImageCompareViewModel()
    {
        LoadDemoData();
        InitializeDefaults();
    }

    public ImageCompareViewModel(IEnumerable<ImageCompareDataset> datasets)
    {
        foreach (var dataset in datasets)
        {
            AvailableDatasets.Add(dataset);
        }

        InitializeDefaults();
    }

    public bool IsAssignBefore
    {
        get => AssignTarget == ImageCompareAssignTarget.Before;
        set
        {
            if (value)
            {
                AssignTarget = ImageCompareAssignTarget.Before;
            }
        }
    }

    public bool IsAssignAfter
    {
        get => AssignTarget == ImageCompareAssignTarget.After;
        set
        {
            if (value)
            {
                AssignTarget = ImageCompareAssignTarget.After;
            }
        }
    }

    [RelayCommand]
    private void SwapImages()
    {
        var previousBefore = SelectedBeforeItem;
        var previousAfter = SelectedAfterItem;

        (BeforeImagePath, AfterImagePath) = (AfterImagePath, BeforeImagePath);
        (BeforeImageLabel, AfterImageLabel) = (AfterImageLabel, BeforeImageLabel);
        (SelectedBeforeItem, SelectedAfterItem) = (previousAfter, previousBefore);
        (BeforeDataset, AfterDataset) = (AfterDataset, BeforeDataset);

        if (previousBefore is not null)
        {
            previousBefore.IsBeforeSelected = false;
            previousBefore.IsAfterSelected = true;
        }

        if (previousAfter is not null)
        {
            previousAfter.IsAfterSelected = false;
            previousAfter.IsBeforeSelected = true;
        }

        RefreshFilmstrip();
    }

    [RelayCommand]
    private void ResetSlider()
    {
        SliderValue = 0.5;
    }

    [RelayCommand]
    private void SetFitMode(ImageCompareFitMode mode)
    {
        FitMode = mode;
    }

    [RelayCommand]
    private void AssignFilmstripItem(ImageCompareItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (AssignTarget == ImageCompareAssignTarget.Before)
        {
            SetBeforeSelection(item);
        }
        else
        {
            SetAfterSelection(item);
        }
    }

    public void RequestTrayOpen()
    {
        _trayCloseCts?.Cancel();
        IsTrayOpen = true;
    }

    public async Task RequestTrayCloseAsync()
    {
        if (IsTrayPinned)
        {
            return;
        }

        _trayCloseCts?.Cancel();
        var cts = new CancellationTokenSource();
        _trayCloseCts = cts;

        try
        {
            await Task.Delay(_trayCloseDelay, cts.Token);
            if (!cts.IsCancellationRequested)
            {
                IsTrayOpen = false;
            }
        }
        catch (TaskCanceledException)
        {
        }
    }

    partial void OnAssignTargetChanged(ImageCompareAssignTarget value)
    {
        OnPropertyChanged(nameof(IsAssignBefore));
        OnPropertyChanged(nameof(IsAssignAfter));
        RefreshFilmstrip();
    }

    partial void OnBeforeDatasetChanged(ImageCompareDataset? value)
    {
        if (value is not null && (SelectedBeforeItem is null || !value.Items.Contains(SelectedBeforeItem)))
        {
            SetBeforeSelection(value.Items.FirstOrDefault());
        }

        RefreshFilmstrip();
    }

    partial void OnAfterDatasetChanged(ImageCompareDataset? value)
    {
        if (value is not null && (SelectedAfterItem is null || !value.Items.Contains(SelectedAfterItem)))
        {
            SetAfterSelection(value.Items.FirstOrDefault());
        }

        RefreshFilmstrip();
    }

    partial void OnIsTrayOpenChanged(bool value)
    {
        TrayHeight = value ? TrayOpenHeight : TrayClosedHeight;
    }

    partial void OnIsTrayPinnedChanged(bool value)
    {
        if (value)
        {
            IsTrayOpen = true;
        }
    }

    partial void OnSearchTextChanged(string? value)
    {
        RefreshFilmstrip();
    }

    private void LoadDemoData()
    {
        var demoBefore = new ImageCompareDataset("Dataset A",
            new ImageCompareItem("Img 014", "C:\\Images\\DatasetA\\img-014.png"),
            new ImageCompareItem("Img 015", "C:\\Images\\DatasetA\\img-015.png"),
            new ImageCompareItem("Img 016", "C:\\Images\\DatasetA\\img-016.png"));

        var demoAfter = new ImageCompareDataset("Dataset B",
            new ImageCompareItem("Img 101", "C:\\Images\\DatasetB\\img-101.png"),
            new ImageCompareItem("Img 102", "C:\\Images\\DatasetB\\img-102.png"),
            new ImageCompareItem("Img 103", "C:\\Images\\DatasetB\\img-103.png"));

        AvailableDatasets.Add(demoBefore);
        AvailableDatasets.Add(demoAfter);
    }

    private void InitializeDefaults()
    {
        BeforeDataset = AvailableDatasets.FirstOrDefault();
        AfterDataset = AvailableDatasets.Skip(1).FirstOrDefault() ?? BeforeDataset;

        SetBeforeSelection(BeforeDataset?.Items.FirstOrDefault());
        SetAfterSelection(AfterDataset?.Items.FirstOrDefault());

        RefreshFilmstrip();
    }

    private void SetBeforeSelection(ImageCompareItem? item)
    {
        if (SelectedBeforeItem is not null)
        {
            SelectedBeforeItem.IsBeforeSelected = false;
        }

        SelectedBeforeItem = item;
        if (item is not null)
        {
            item.IsBeforeSelected = true;
            BeforeImagePath = item.ImagePath;
            BeforeImageLabel = BuildLabel(BeforeDataset, item);
        }
    }

    private void SetAfterSelection(ImageCompareItem? item)
    {
        if (SelectedAfterItem is not null)
        {
            SelectedAfterItem.IsAfterSelected = false;
        }

        SelectedAfterItem = item;
        if (item is not null)
        {
            item.IsAfterSelected = true;
            AfterImagePath = item.ImagePath;
            AfterImageLabel = BuildLabel(AfterDataset, item);
        }
    }

    private void RefreshFilmstrip()
    {
        var sourceItems = AssignTarget == ImageCompareAssignTarget.Before
            ? BeforeDataset?.Items
            : AfterDataset?.Items;

        FilmstripItems.Clear();

        if (sourceItems is null)
        {
            return;
        }

        var filter = SearchText?.Trim();
        foreach (var item in sourceItems)
        {
            if (!string.IsNullOrWhiteSpace(filter) && !item.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            FilmstripItems.Add(item);
        }
    }

    private static string BuildLabel(ImageCompareDataset? dataset, ImageCompareItem item)
    {
        if (dataset is null)
        {
            return item.DisplayName;
        }

        return $"{dataset.Name} / {item.DisplayName}";
    }
}

public partial class ImageCompareItem : ObservableObject
{
    public ImageCompareItem(string displayName, string imagePath)
    {
        DisplayName = displayName;
        ImagePath = imagePath;
    }

    public string DisplayName { get; }
    public string ImagePath { get; }

    [ObservableProperty]
    private bool _isBeforeSelected;

    [ObservableProperty]
    private bool _isAfterSelected;
}

public class ImageCompareDataset
{
    public ImageCompareDataset(string name, params ImageCompareItem[] items)
    {
        Name = name;
        Items = new ObservableCollection<ImageCompareItem>(items);
    }

    public string Name { get; }
    public ObservableCollection<ImageCompareItem> Items { get; }
}
