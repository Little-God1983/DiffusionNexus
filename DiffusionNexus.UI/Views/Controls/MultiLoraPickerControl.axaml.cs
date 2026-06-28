using System.Collections;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.ViewModels.Controls;

namespace DiffusionNexus.UI.Views.Controls;

/// <summary>
/// Reusable multi-LoRA picker. Shows one row per <see cref="LoraPickerItemViewModel"/> in the bound
/// <see cref="Loras"/> collection — each with a searchable LoRA dropdown (fed by <see cref="AvailableLoras"/>),
/// an on/off toggle, and a -2…2 strength slider — plus a "＋ Add a LoRA" button that appends a new
/// optional row (the button stays put; rows grow above it). Workflow-mandated rows (<see
/// cref="LoraPickerItemViewModel.IsMandatory"/>) can't be toggled off or removed, only re-strengthened.
/// The control mutates the bound <see cref="Loras"/> collection in place, so the owning ViewModel
/// observes adds/removes directly.
/// </summary>
public partial class MultiLoraPickerControl : UserControl
{
    /// <summary>The LoRA rows (mandatory + user-added). Mutated in place by the control.</summary>
    public static readonly StyledProperty<ObservableCollection<LoraPickerItemViewModel>?> LorasProperty =
        AvaloniaProperty.Register<MultiLoraPickerControl, ObservableCollection<LoraPickerItemViewModel>?>(nameof(Loras));

    /// <summary>The installed LoRAs offered in each row's search dropdown (typically already base-model filtered).</summary>
    public static readonly StyledProperty<IEnumerable?> AvailableLorasProperty =
        AvaloniaProperty.Register<MultiLoraPickerControl, IEnumerable?>(nameof(AvailableLoras));

    public ObservableCollection<LoraPickerItemViewModel>? Loras
    {
        get => GetValue(LorasProperty);
        set => SetValue(LorasProperty, value);
    }

    public IEnumerable? AvailableLoras
    {
        get => GetValue(AvailableLorasProperty);
        set => SetValue(AvailableLorasProperty, value);
    }

    /// <summary>Bound by the "＋ Add a LoRA" button.</summary>
    public ICommand AddLoraCommand { get; }

    /// <summary>Bound by each optional row's remove button.</summary>
    public ICommand RemoveLoraCommand { get; }

    public MultiLoraPickerControl()
    {
        AddLoraCommand = new RelayCommand(AddLora);
        RemoveLoraCommand = new RelayCommand<LoraPickerItemViewModel>(RemoveLora);
        InitializeComponent();
    }

    private void AddLora()
    {
        var target = Loras;
        if (target is null)
        {
            target = [];
            Loras = target;
        }

        target.Add(new LoraPickerItemViewModel { Strength = 1.0 });
    }

    private void RemoveLora(LoraPickerItemViewModel? item)
    {
        if (item is null || item.IsMandatory)
            return;
        Loras?.Remove(item);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
