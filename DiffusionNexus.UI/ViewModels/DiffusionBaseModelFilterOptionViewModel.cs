using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffusionNexus.UI.ViewModels;

public partial class DiffusionBaseModelFilterOptionViewModel : ObservableObject
{
    private readonly Action _selectionChanged;

    public DiffusionBaseModelFilterOptionViewModel(
        string displayName,
        string filterKey,
        Action selectionChanged)
    {
        DisplayName = displayName;
        FilterKey = filterKey;
        _selectionChanged = selectionChanged;
    }

    public string DisplayName { get; }
    public string FilterKey { get; }

    [ObservableProperty]
    private bool isSelected;

    partial void OnIsSelectedChanged(bool value) => _selectionChanged?.Invoke();
}
