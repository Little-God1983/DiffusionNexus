using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Service.Classes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DiffusionNexus.UI.ViewModels;

public partial class LoraCardViewModel : ViewModelBase
{
    [ObservableProperty]
    private string? _description;

    [ObservableProperty]
    private ModelClass? _model;

    [ObservableProperty]
    private Bitmap? _previewImage;

    [ObservableProperty]
    private string? folderPath;

    public IEnumerable<string> DiffusionTypes => Model is null
        ? Array.Empty<string>()
        : new[] { Model.ModelType.ToString() };

    public string DiffusionBaseModel => Model?.DiffusionBaseModel ?? string.Empty;

    public IRelayCommand EditCommand { get; }
    public IAsyncRelayCommand DeleteCommand { get; }
    public IRelayCommand OpenWebCommand { get; }
    public IRelayCommand CopyCommand { get; }

    public LoraHelperViewModel? Parent { get; set; }

    public LoraCardViewModel()
    {
        EditCommand = new RelayCommand(OnEdit);
        DeleteCommand = new AsyncRelayCommand(OnDeleteAsync);
        OpenWebCommand = new RelayCommand(OnOpenWeb);
        CopyCommand = new RelayCommand(OnCopy);
    }

    partial void OnModelChanged(ModelClass? value)
    {
        _ = LoadPreviewImageAsync();
    }

    private async Task LoadPreviewImageAsync()
    {
        var path = GetPreviewImagePath();
        if (path is null || !File.Exists(path))
        {
            PreviewImage = null;
            return;
        }

        try
        {
            var bitmap = await Task.Run(() =>
            {
                using var stream = File.OpenRead(path);
                return new Bitmap(stream);
            });
            await Dispatcher.UIThread.InvokeAsync(() => PreviewImage = bitmap);
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() => PreviewImage = null);
        }
    }

    public string? GetPreviewImagePath()
    {
        if (Model == null) return null;
        string[] priority = [
            ".thumb.jpg",
            ".webp",
            "jpeg",
            "jpg",
            ".preview.webp",
            ".preview.jpeg",
            ".preview.jpg",
            ".preview.png",
        ];

        foreach (var ext in priority)
        {
            var file = Model.AssociatedFilesInfo.FirstOrDefault(f => f.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
            if (file != null)
                return file.FullName;
        }
        
        return null;
    }

    private void OnEdit() => Log($"Edit {Model.SafeTensorFileName}", LogSeverity.Info);

    private Task OnDeleteAsync()
    {
        return Parent?.DeleteCardAsync(this) ?? Task.CompletedTask;
    }

    private void OnOpenWeb() => Log($"Open web for {Model.SafeTensorFileName}", LogSeverity.Info);

    private void OnCopy() => Log($"Copy {Model.SafeTensorFileName}", LogSeverity.Info);
}
