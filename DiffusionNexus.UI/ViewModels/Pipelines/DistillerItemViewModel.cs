using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.UI.Models;

namespace DiffusionNexus.UI.ViewModels.Pipelines;

/// <summary>One input image: its parsed metadata, editable working copies, and detected LoRAs.</summary>
public partial class DistillerItemViewModel : ViewModelBase
{
    private readonly ImageGenerationData _data;

    public string Path { get; }
    public string FileName { get; }
    public bool HasMetadata { get; }
    public bool HasLoras { get; }
    public bool IsPng { get; }

    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private bool _includeInRun;

    [ObservableProperty] private string? _positive;
    [ObservableProperty] private string? _negative;
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

    /// <summary>Loads a small preview off the UI thread. Safe to skip in tests.</summary>
    public async Task LoadThumbnailAsync()
    {
        try
        {
            var bmp = await Task.Run(() =>
            {
                using var fs = File.OpenRead(Path);
                return Bitmap.DecodeToWidth(fs, 200);
            });
            Thumbnail = bmp;
        }
        catch { /* undecodable — leave placeholder */ }
    }

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
}
