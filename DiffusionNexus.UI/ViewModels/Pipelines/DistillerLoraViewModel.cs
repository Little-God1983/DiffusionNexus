using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.UI.Models;

namespace DiffusionNexus.UI.ViewModels.Pipelines;

/// <summary>One detected LoRA row in the metadata editor.</summary>
public partial class DistillerLoraViewModel : ViewModelBase
{
    public string Name { get; }
    public string? SourceLabel { get; }

    [ObservableProperty] private double _strength;
    [ObservableProperty] private bool _include = true;

    public DistillerLoraViewModel(LoraInfo info)
    {
        Name = info.Name;
        SourceLabel = info.Source;
        _strength = info.StrengthModel;
    }

    public LoraInfo ToLoraInfo() => new() { Name = Name, StrengthModel = Strength, StrengthClip = Strength, Source = SourceLabel };
}
