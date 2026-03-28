using System.IO;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Utilities;
using DiffusionNexus.Domain.Entities; // Assuming for some enums if needed, check imports later if failed
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using SkiaSharp;

namespace DiffusionNexus.UI.ViewModels;

public enum ReplaceAction
{
    None,
    Replace,
    AddAsNew
}

public class ReplaceImageResult
{
    public bool Confirmed { get; init; }
    public ReplaceAction Action { get; init; }
    public string? NewFilePath { get; init; }

    public static ReplaceImageResult Cancelled() => new() { Confirmed = false, Action = ReplaceAction.None };
}

public partial class ReplaceImageDialogViewModel : ObservableObject
{
    private readonly DatasetImageViewModel _originalImage;

    [ObservableProperty]
    private Bitmap? _originalThumbnail;

    [ObservableProperty]
    private Bitmap? _newThumbnail;

    [ObservableProperty]
    private string _originalFileSize = string.Empty;

    [ObservableProperty]
    private string _newFileSize = string.Empty;

    [ObservableProperty]
    private string _originalResolution = string.Empty;

    [ObservableProperty]
    private string _newResolution = string.Empty;

    [ObservableProperty]
    private string _originalDate = string.Empty;

    [ObservableProperty]
    private string _newDate = string.Empty;

    [ObservableProperty]
    private string _newFileName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReplaceCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddAsNewCommand))]
    [NotifyPropertyChangedFor(nameof(HasNewFile))]
    [NotifyPropertyChangedFor(nameof(HasNoNewFile))]
    private string? _newFilePath;

    public bool HasNewFile => !string.IsNullOrEmpty(NewFilePath);
    public bool HasNoNewFile => string.IsNullOrEmpty(NewFilePath);

    public bool CanConfirm => HasNewFile;

    public ReplaceImageDialogViewModel(DatasetImageViewModel originalImage)
    {
        _originalImage = originalImage;
        LoadOriginalInfo();
    }

    // Default constructor for design time
    public ReplaceImageDialogViewModel()
    {
        _originalImage = new DatasetImageViewModel(); 
    }

    private async void LoadOriginalInfo()
    {
        if (System.IO.File.Exists(_originalImage.ImagePath))
        {
             var info = new FileInfo(_originalImage.ImagePath);
             OriginalFileSize = $"Size: {FormatFileSize(info.Length)}";
             OriginalDate = $"Date: {info.CreationTime.ToShortDateString()} {info.CreationTime.ToShortTimeString()}";

             if (_originalImage.IsImage)
             {
                 try
                 {
                     // Read resolution without full decode
                     var resolution = await Task.Run(() => GetImageDimensions(_originalImage.ImagePath));
                     if (resolution.HasValue)
                         OriginalResolution = $"Resolution: {resolution.Value.Width} x {resolution.Value.Height}";
                     else
                         OriginalResolution = "Resolution: Unknown";

                     var bitmap = await Task.Run(() => EfficientImageDecoder.DecodeThumbnail(_originalImage.ImagePath, 400));
                     OriginalThumbnail = bitmap;
                 }
                 catch { OriginalResolution = "Resolution: Unknown"; }
             }
             else if (_originalImage.IsVideo)
             {
                 if (!string.IsNullOrEmpty(_originalImage.ThumbnailPath) && File.Exists(_originalImage.ThumbnailPath))
                 {
                      var bitmap = await Task.Run(() => EfficientImageDecoder.DecodeThumbnail(_originalImage.ThumbnailPath, 400));
                      OriginalThumbnail = bitmap;
                 }
                 OriginalResolution = "Resolution: Video"; 
             }
        }
    }

    public async Task SetNewFileAsync(string filePath)
    {
        if (!File.Exists(filePath)) return;

        NewFilePath = filePath;
        NewFileName = Path.GetFileName(filePath);

        var info = new FileInfo(filePath);
        NewFileSize = $"Size: {FormatFileSize(info.Length)}";
        NewDate = $"Date: {info.CreationTime.ToShortDateString()} {info.CreationTime.ToShortTimeString()}";

        // Check extensions to match original type broadly
        bool isImage = MediaFileExtensions.IsImageFile(filePath);
        bool isVideo = MediaFileExtensions.IsVideoFile(filePath);

        if (isImage)
        {
             try
             {
                 // Read resolution without full decode
                 var resolution = await Task.Run(() => GetImageDimensions(filePath));
                 if (resolution.HasValue)
                     NewResolution = $"Resolution: {resolution.Value.Width} x {resolution.Value.Height}";
                 else
                     NewResolution = "Resolution: Unknown";

                 var bitmap = await Task.Run(() => EfficientImageDecoder.DecodeThumbnail(filePath, 400));
                 NewThumbnail = bitmap;
             }
             catch { 
                NewResolution = "Resolution: Unknown"; 
                NewThumbnail = null;
             }
        }
        else if (isVideo)
        {
            // Just show icon or empty for now, generating video thumbnail is async service specific
            NewResolution = "Resolution: Video"; 
            NewThumbnail = null; 
        }
        else
        {
            NewResolution = "Resolution: Unknown Type";
            NewThumbnail = null;
        }
    }

    private string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    /// <summary>
    /// Reads the original image dimensions using SKCodec without decoding the full pixel data.
    /// </summary>
    private static (int Width, int Height)? GetImageDimensions(string filePath)
    {
        try
        {
            using var stream = System.IO.File.OpenRead(filePath);
            using var codec = SKCodec.Create(stream);
            if (codec is null) return null;
            return (codec.Info.Width, codec.Info.Height);
        }
        catch
        {
            return null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Replace(IDialogCloseable window)
    {
        window?.Close(new ReplaceImageResult 
        { 
            Confirmed = true, 
            Action = ReplaceAction.Replace, 
            NewFilePath = NewFilePath 
        });
    }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void AddAsNew(IDialogCloseable window)
    {
        window?.Close(new ReplaceImageResult 
        { 
            Confirmed = true, 
            Action = ReplaceAction.AddAsNew, 
            NewFilePath = NewFilePath 
        });
    }

    [RelayCommand]
    private void Cancel(IDialogCloseable window)
    {
        window?.Close(ReplaceImageResult.Cancelled());
    }
}

// Interface for window closing
public interface IDialogCloseable
{
    void Close(object? result);
}
