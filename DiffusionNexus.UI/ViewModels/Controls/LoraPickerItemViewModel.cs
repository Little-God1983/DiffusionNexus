using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.UI.Services.Lora;

namespace DiffusionNexus.UI.ViewModels.Controls;

/// <summary>
/// One row in the reusable <see cref="Views.Controls.MultiLoraPickerControl"/>: a chosen (or workflow-
/// mandated) LoRA with an on/off toggle and a strength. Mandatory rows are supplied by a workflow and
/// cannot be disabled or removed — only their strength is adjustable.
/// </summary>
public partial class LoraPickerItemViewModel : ObservableObject
{
    /// <summary>Display name (the picked LoRA's name, or the workflow's name for mandatory rows).</summary>
    [ObservableProperty] private string _displayName = string.Empty;

    /// <summary>Resolved weights path. For optional rows it comes from <see cref="SelectedLora"/>; for
    /// mandatory rows it's resolved at generation time from <see cref="CivitaiModelId"/>.</summary>
    [ObservableProperty] private string? _filePath;

    /// <summary>The LoRA picked from the searchable dropdown (optional rows only).</summary>
    [ObservableProperty] private AvailableLora? _selectedLora;

    /// <summary>Whether this LoRA is applied. Always true (and locked) for mandatory rows.</summary>
    [ObservableProperty] private bool _isEnabled = true;

    /// <summary>Strength multiplier, -2.0 … 2.0.</summary>
    [ObservableProperty] private double _strength = 1.0;

    /// <summary>Mandatory rows come from the workflow manifest; they can't be toggled off or removed.</summary>
    public bool IsMandatory { get; init; }

    /// <summary>For mandatory rows: the Civitai model id used to resolve the file path on disk.</summary>
    public int? CivitaiModelId { get; init; }

    partial void OnSelectedLoraChanged(AvailableLora? value)
    {
        // AutoCompleteBox nulls SelectedItem when the typed text no longer matches a committed pick;
        // clear the resolved path too so a cleared/changed row never applies a stale LoRA.
        if (value is null)
        {
            FilePath = null;
            return;
        }
        FilePath = value.FilePath;
        DisplayName = value.DisplayName;
    }
}
