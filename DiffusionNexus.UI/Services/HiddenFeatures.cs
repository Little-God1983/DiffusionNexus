using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Whether the app's unofficial features are revealed — the faint checkbox at the bottom of the
/// sidebar (<see cref="ViewModels.DiffusionNexusMainWindowViewModel.IsHiddenFeaturesEnabled"/>).
/// One app-wide instance (DI singleton), so any feature can follow the same switch: the Diffusion
/// Canvas entry, the LoRA Sorter's "Browse any folder…" (#653). Off at every start, never saved.
/// </summary>
public sealed partial class HiddenFeatures : ObservableObject
{
    [ObservableProperty]
    private bool _isEnabled;
}
