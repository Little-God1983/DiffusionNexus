using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Service.Classes;
using DiffusionNexus.UI.Classes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
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
    public IAsyncRelayCommand OpenWebCommand { get; }
    public IAsyncRelayCommand CopyCommand { get; }
    public IAsyncRelayCommand CopyNameCommand { get; }
    public IRelayCommand OpenFolderCommand { get; }

    public LoraHelperViewModel? Parent { get; set; }

    public LoraCardViewModel()
    {
        EditCommand = new RelayCommand(OnEdit);
        DeleteCommand = new AsyncRelayCommand(OnDeleteAsync);
        OpenWebCommand = new AsyncRelayCommand(OnOpenWebAsync);
        CopyCommand = new AsyncRelayCommand(OnCopyAsync);
        CopyNameCommand = new AsyncRelayCommand(OnCopyNameAsync);
        OpenFolderCommand = new RelayCommand(OnOpenFolder);
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
            var media = GetPreviewMediaPath();
            if (media is not null && ThumbnailSettings.GenerateVideoThumbnails)
            {
                path = await ThumbnailGenerator.GenerateThumbnailAsync(media);
            }
        }

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
      
        foreach (var ext in SupportedTypes.ImageTypesByPriority)
        {
            var file = Model.AssociatedFilesInfo.FirstOrDefault(f => f.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
            if (file != null)
                return file.FullName;
        }
        
        return null;
    }

    private string? GetPreviewMediaPath()
    {
        if (Model == null) return null;
        

        foreach (var ext in SupportedTypes.VideoTypesByPriority)
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

    private async Task OnOpenWebAsync()
    {
        if (Parent == null || Model == null)
            return;

        await Parent.OpenWebForCardAsync(this);
    }

    private async Task OnCopyAsync()
    {
        if (Parent == null || Model == null)
            return;

        await Parent.CopyTrainedWordsAsync(this);
    }

    private async Task OnCopyNameAsync()
    {
        if (Parent == null || Model == null)
            return;

        await Parent.CopyModelNameAsync(this);
    }

    private void OnOpenFolder()
    {
        if (string.IsNullOrWhiteSpace(FolderPath))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = FolderPath,
                UseShellExecute = true,
                Verb = "open"
            });
        }
        catch (Exception ex)
        {
            Log($"failed to open folder: {ex.Message}", LogSeverity.Error);
        }
    }
}
