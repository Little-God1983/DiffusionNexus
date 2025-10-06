using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace DiffusionNexus.UI.ViewModels;

public partial class DiffusionModelFilterOptionViewModel : ObservableObject
{
    private bool _suppressNotifications;

    public DiffusionModelFilterOptionViewModel(string name)
    {
        DisplayName = name;
    }

    public string DisplayName { get; }

    [ObservableProperty]
    private bool isSelected;

    internal event EventHandler? SelectionChanged;

    internal void SetIsSelectedSilently(bool value)
    {
        try
        {
            _suppressNotifications = true;
            IsSelected = value;
        }
        finally
        {
            _suppressNotifications = false;
        }
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (!_suppressNotifications)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
