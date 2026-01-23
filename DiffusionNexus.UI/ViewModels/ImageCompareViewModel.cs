using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.Controls;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the before/after image comparer experience.
/// </summary>
public partial class ImageCompareViewModel : ViewModelBase
{
    private readonly Dictionary<string, ObservableCollection<ImageCompareItem>> _datasets;
    private ImageCompareItem? _selectedBeforeImage;
    private ImageCompareItem? _selectedAfterImage;

    public ImageCompareViewModel()
        : this(new Dictionary<string, IEnumerable<ImageCompareItem>>
        {
            { "Dataset A", [] },
            { "Dataset B", [] }
        })
    {
    }

    public ImageCompareViewModel(IReadOnlyDictionary<string, IEnumerable<ImageCompareItem>> datasets)
    {
        _datasets = datasets.ToDictionary(
            pair => pair.Key,
            pair => new ObservableCollection<ImageCompareItem>(pair.Value));

        DatasetOptions = new ObservableCollection<string>(_datasets.Keys);
        FitModeOptions = new List<CompareFitMode> { CompareFitMode.Fit, CompareFitMode.Fill, CompareFitMode.OneToOne };
        FilmstripItems = new ObservableCollection<ImageCompareItem>();

        SelectedBeforeDataset = DatasetOptions.FirstOrDefault();
        SelectedAfterDataset = DatasetOptions.Skip(1).FirstOrDefault() ?? SelectedBeforeDataset;

        EnsureSelectedImages();
        AssignSide = CompareAssignSide.Before;
        FitMode = CompareFitMode.Fit;
        SliderValue = 50d;
        IsTrayOpen = false;

        SwapCommand = new RelayCommand(SwapImages, () => SelectedBeforeImage is not null || SelectedAfterImage is not null);
        ResetSliderCommand = new RelayCommand(ResetSlider);
        AssignImageCommand = new RelayCommand<ImageCompareItem?>(AssignImage);

        RefreshFilmstrip();
    }

    public ObservableCollection<string> DatasetOptions { get; }

    public ObservableCollection<ImageCompareItem> FilmstripItems { get; }

    public IReadOnlyList<CompareFitMode> FitModeOptions { get; }

    public IRelayCommand SwapCommand { get; }

    public IRelayCommand ResetSliderCommand { get; }

    public IRelayCommand<ImageCompareItem?> AssignImageCommand { get; }

    [ObservableProperty]
    private string? _selectedBeforeDataset;

    [ObservableProperty]
    private string? _selectedAfterDataset;

    [ObservableProperty]
    private CompareAssignSide _assignSide;

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    private CompareFitMode _fitMode;

    [ObservableProperty]
    private double _sliderValue;

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    private bool _isTrayOpen;

    public ImageCompareItem? SelectedBeforeImage
    {
        get => _selectedBeforeImage;
        set
        {
            if (SetProperty(ref _selectedBeforeImage, value))
            {
                UpdateSelectionFlags(value, isBefore: true);
                OnPropertyChanged(nameof(BeforeImagePath));
                OnPropertyChanged(nameof(BeforeLabel));
                SwapCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public ImageCompareItem? SelectedAfterImage
    {
        get => _selectedAfterImage;
        set
        {
            if (SetProperty(ref _selectedAfterImage, value))
            {
                UpdateSelectionFlags(value, isBefore: false);
                OnPropertyChanged(nameof(AfterImagePath));
                OnPropertyChanged(nameof(AfterLabel));
                SwapCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string? BeforeImagePath => SelectedBeforeImage?.ImagePath;

    public string? AfterImagePath => SelectedAfterImage?.ImagePath;

    public string BeforeLabel => BuildLabel(SelectedBeforeDataset, SelectedBeforeImage?.DisplayName, "Before");

    public string AfterLabel => BuildLabel(SelectedAfterDataset, SelectedAfterImage?.DisplayName, "After");

    public double TrayHeight => 260d;

    public double TrayHandleHeight => 34d;

    public double TrayVisibleHeight => IsTrayOpen ? TrayHeight : TrayHandleHeight;

    public bool IsAssigningBefore
    {
        get => AssignSide == CompareAssignSide.Before;
        set
        {
            if (value)
            {
                AssignSide = CompareAssignSide.Before;
            }
        }
    }

    public bool IsAssigningAfter
    {
        get => AssignSide == CompareAssignSide.After;
        set
        {
            if (value)
            {
                AssignSide = CompareAssignSide.After;
            }
        }
    }

    partial void OnSelectedBeforeDatasetChanged(string? value)
    {
        EnsureSelectedImages();
        if (AssignSide == CompareAssignSide.Before)
        {
            RefreshFilmstrip();
        }

        OnPropertyChanged(nameof(BeforeLabel));
    }

    partial void OnSelectedAfterDatasetChanged(string? value)
    {
        EnsureSelectedImages();
        if (AssignSide == CompareAssignSide.After)
        {
            RefreshFilmstrip();
        }

        OnPropertyChanged(nameof(AfterLabel));
    }

    partial void OnAssignSideChanged(CompareAssignSide value)
    {
        OnPropertyChanged(nameof(IsAssigningBefore));
        OnPropertyChanged(nameof(IsAssigningAfter));
        RefreshFilmstrip();
    }

    partial void OnSearchTextChanged(string? value)
    {
        RefreshFilmstrip();
    }

    partial void OnIsTrayOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(TrayVisibleHeight));
    }

    partial void OnIsPinnedChanged(bool value)
    {
        if (value)
        {
            IsTrayOpen = true;
        }
    }

    public void ResetSlider()
    {
        SliderValue = 50d;
    }

    public void SwapImages()
    {
        var before = SelectedBeforeImage;
        var after = SelectedAfterImage;
        SelectedBeforeImage = after;
        SelectedAfterImage = before;
    }

    public void AssignImage(ImageCompareItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (AssignSide == CompareAssignSide.Before)
        {
            SelectedBeforeImage = item;
        }
        else
        {
            SelectedAfterImage = item;
        }
    }

    private void EnsureSelectedImages()
    {
        if (SelectedBeforeDataset is not null && _datasets.TryGetValue(SelectedBeforeDataset, out var beforeImages))
        {
            if (SelectedBeforeImage is null || !beforeImages.Contains(SelectedBeforeImage))
            {
                SelectedBeforeImage = beforeImages.FirstOrDefault();
            }
        }

        if (SelectedAfterDataset is not null && _datasets.TryGetValue(SelectedAfterDataset, out var afterImages))
        {
            if (SelectedAfterImage is null || !afterImages.Contains(SelectedAfterImage))
            {
                SelectedAfterImage = afterImages.FirstOrDefault();
            }
        }
    }

    private void RefreshFilmstrip()
    {
        FilmstripItems.Clear();
        var datasetName = AssignSide == CompareAssignSide.Before ? SelectedBeforeDataset : SelectedAfterDataset;
        if (datasetName is null || !_datasets.TryGetValue(datasetName, out var images))
        {
            return;
        }

        var query = SearchText?.Trim();
        foreach (var item in images)
        {
            if (string.IsNullOrWhiteSpace(query) || item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                FilmstripItems.Add(item);
            }
        }
    }

    private void UpdateSelectionFlags(ImageCompareItem? newSelection, bool isBefore)
    {
        if (isBefore)
        {
            foreach (var dataset in _datasets.Values)
            {
                foreach (var item in dataset)
                {
                    if (item.IsSelectedBefore)
                    {
                        item.IsSelectedBefore = false;
                    }
                }
            }

            if (newSelection is not null)
            {
                newSelection.IsSelectedBefore = true;
            }
        }
        else
        {
            foreach (var dataset in _datasets.Values)
            {
                foreach (var item in dataset)
                {
                    if (item.IsSelectedAfter)
                    {
                        item.IsSelectedAfter = false;
                    }
                }
            }

            if (newSelection is not null)
            {
                newSelection.IsSelectedAfter = true;
            }
        }
    }

    private static string BuildLabel(string? datasetName, string? imageName, string fallback)
    {
        var dataset = string.IsNullOrWhiteSpace(datasetName) ? fallback : datasetName;
        var image = string.IsNullOrWhiteSpace(imageName) ? "Unassigned" : imageName;
        return $"{dataset} / {image}";
    }
}

public enum CompareAssignSide
{
    Before,
    After
}
