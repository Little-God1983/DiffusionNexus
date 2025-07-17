using Avalonia.Controls;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Service.Classes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace DiffusionNexus.UI.ViewModels;

public partial class LoraCardDetailViewModel : ViewModelBase
{
    private ModelClass? _model;
    private Window? _window;

    [ObservableProperty]
    private string? name;

    [ObservableProperty]
    private string? modelVersionName;

    [ObservableProperty]
    private DiffusionTypes modelType;

    [ObservableProperty]
    private string? tags;

    [ObservableProperty]
    private string? trainedWords;

    [ObservableProperty]
    private string? safetensorFileName;

    [ObservableProperty]
    private Bitmap? previewImage;

    public Array DiffusionTypesList => Enum.GetValues(typeof(DiffusionTypes));

    public IAsyncRelayCommand SaveCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public LoraCardDetailViewModel()
    {
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        CancelCommand = new RelayCommand(Cancel);
    }

    public void SetWindow(Window window) => _window = window;

    public void SetModel(ModelClass model)
    {
        _model = model;
        Name = model.ModelVersionName;
        ModelVersionName = model.ModelVersionName;
        ModelType = model.ModelType;
        Tags = string.Join(", ", model.Tags);
        TrainedWords = string.Join(", ", model.TrainedWords);
        SafetensorFileName = model.SafeTensorFileName;
        _ = LoadPreviewAsync();
    }

    private void Cancel() => _window?.Close(false);

    private async Task SaveAsync()
    {
        if (_model == null)
        {
            _window?.Close(false);
            return;
        }

        _model.ModelVersionName = ModelVersionName ?? _model.ModelVersionName;
        _model.ModelType = ModelType;
        _model.Tags = (Tags ?? string.Empty)
            .Split(',', ';', '\n', '\r', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
        _model.TrainedWords = (TrainedWords ?? string.Empty)
            .Split(',', ';', '\n', '\r', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        var folder = _model.AssociatedFilesInfo.FirstOrDefault()?.DirectoryName;
        if (folder != null)
        {
            var jsonFile = _model.AssociatedFilesInfo
                .FirstOrDefault(f => f.Name.EndsWith("civitai.json", StringComparison.OrdinalIgnoreCase))
                ?.FullName ?? Path.Combine(folder, _model.SafeTensorFileName + ".civitai.json");

            var json = JsonSerializer.Serialize(new
            {
                name = Name,
                modelVersionName = ModelVersionName,
                type = ModelType.ToString(),
                tags = _model.Tags,
                trainedWords = _model.TrainedWords
            }, new JsonSerializerOptions { WriteIndented = true });

            await File.WriteAllTextAsync(jsonFile, json);
        }

        _window?.Close(true);
    }

    private async Task LoadPreviewAsync()
    {
        var path = GetPreviewImagePath();
        if (path == null || !File.Exists(path))
        {
            PreviewImage = null;
            return;
        }

        await using var stream = File.OpenRead(path);
        var bmp = await Task.Run(() => new Bitmap(stream));
        PreviewImage = bmp;
    }

    private string? GetPreviewImagePath()
    {
        if (_model == null)
            return null;
        foreach (var ext in SupportedTypes.ImageTypesByPriority)
        {
            var file = _model.AssociatedFilesInfo.FirstOrDefault(f => f.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
            if (file != null)
                return file.FullName;
        }
        return null;
    }
}
