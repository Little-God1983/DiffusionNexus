using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Service.Classes;
using System;

namespace DiffusionNexus.UI.ViewModels;

public partial class LoraVariantViewModel : ObservableObject
{
    private readonly Action<LoraVariantViewModel> _onSelected;

    public LoraVariantViewModel(string label, ModelClass model, Action<LoraVariantViewModel> onSelected)
    {
        Label = label;
        Model = model;
        _onSelected = onSelected ?? throw new ArgumentNullException(nameof(onSelected));
        SelectCommand = new RelayCommand(() => _onSelected(this));
    }

    public string Label { get; }

    public ModelClass Model { get; }

    [ObservableProperty]
    private bool isSelected;

    public IRelayCommand SelectCommand { get; }
}
