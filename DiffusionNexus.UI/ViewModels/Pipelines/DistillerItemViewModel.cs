using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.Controls;
using DiffusionNexus.UI.Models;

namespace DiffusionNexus.UI.ViewModels.Pipelines;

/// <summary>One input image: its parsed metadata, editable working copies, and detected LoRAs.</summary>
public partial class DistillerItemViewModel : ViewModelBase, System.IDisposable
{
    private readonly ImageGenerationData _data;

    public string Path { get; }
    public string FileName { get; }
    public bool HasMetadata { get; }
    public bool HasLoras { get; }
    public bool IsPng { get; }

    [ObservableProperty] private bool _includeInRun;

    [ObservableProperty] private string? _positive;
    [ObservableProperty] private string? _negative;

    /// <summary>The prompts as originally parsed from the image — the "undo edits" restore point.</summary>
    public string? OriginalPositive { get; }
    public string? OriginalNegative { get; }

    [ObservableProperty] private bool _isPositiveModified;
    [ObservableProperty] private bool _isNegativeModified;

    /// <summary>Rule-match spans shown by the prompt editors after a rules "Test" run.</summary>
    [ObservableProperty] private IReadOnlyList<TextHighlightRange>? _positiveHighlights;
    [ObservableProperty] private IReadOnlyList<TextHighlightRange>? _negativeHighlights;
    [ObservableProperty] private string _stepsText = "";
    [ObservableProperty] private string _cfgText = "";
    [ObservableProperty] private string _seedText = "";
    [ObservableProperty] private string? _samplerName;
    [ObservableProperty] private string? _scheduler;
    [ObservableProperty] private string? _model;

    public ObservableCollection<DistillerLoraViewModel> Loras { get; } = [];

    public DistillerItemViewModel(string path, ImageGenerationData data)
    {
        _data = data;
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        IsPng = string.Equals(System.IO.Path.GetExtension(path), ".png", System.StringComparison.OrdinalIgnoreCase);
        HasMetadata = data.HasData;
        HasLoras = data.Loras.Count > 0;
        IncludeInRun = data.HasData && IsPng; // v1 writes PNG output only

        // Originals must be set before Positive/Negative so the modified-flag comparison sees them.
        OriginalPositive = data.PositivePrompt;
        OriginalNegative = data.NegativePrompt;
        Positive = data.PositivePrompt;
        Negative = data.NegativePrompt;
        StepsText = data.Steps?.ToString(CultureInfo.InvariantCulture) ?? "";
        CfgText = data.Cfg?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
        SeedText = data.Seed?.ToString(CultureInfo.InvariantCulture) ?? "";
        SamplerName = data.SamplerName;
        Scheduler = data.Scheduler;
        Model = data.Checkpoint;

        foreach (var lora in data.Loras)
            Loras.Add(new DistillerLoraViewModel(lora));
    }

    // Track divergence from the parsed original; any edit invalidates a previous rules-test highlight
    // because the recorded match positions no longer line up with the text.
    partial void OnPositiveChanged(string? value)
    {
        IsPositiveModified = !string.Equals(value ?? "", OriginalPositive ?? "", System.StringComparison.Ordinal);
        PositiveHighlights = null;
    }

    partial void OnNegativeChanged(string? value)
    {
        IsNegativeModified = !string.Equals(value ?? "", OriginalNegative ?? "", System.StringComparison.Ordinal);
        NegativeHighlights = null;
    }

    /// <summary>Restores the positive prompt to what was parsed from the image.</summary>
    [RelayCommand]
    private void UndoPositive() => Positive = OriginalPositive;

    /// <summary>Restores the negative prompt to what was parsed from the image.</summary>
    [RelayCommand]
    private void UndoNegative() => Negative = OriginalNegative;

    /// <summary>Applies the user's numeric/text edits back onto a copy of the parsed data.</summary>
    public ImageGenerationData BuildEditedData() => _data with
    {
        PositivePrompt = Positive,
        NegativePrompt = Negative,
        Steps = int.TryParse(StepsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null,
        Cfg = double.TryParse(CfgText, NumberStyles.Float, CultureInfo.InvariantCulture, out var c) ? c : null,
        Seed = long.TryParse(SeedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sd) ? sd : null,
        SamplerName = SamplerName,
        Scheduler = Scheduler,
        Checkpoint = Model,
    };

    public IReadOnlyList<LoraInfo> IncludedLoras() =>
        Loras.Where(l => l.Include).Select(l => l.ToLoraInfo()).ToList();

    // Thumbnails are rendered by ImageListInputControl (off the UI thread), not here; this type owns
    // no unmanaged resources. Dispose is kept so the owning collection can dispose items uniformly.
    public void Dispose() { }
}
