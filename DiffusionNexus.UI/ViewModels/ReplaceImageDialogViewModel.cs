using System.IO;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.Utilities;
using DiffusionNexus.Domain.Entities; // Assuming for some enums if needed, check imports later if failed
using System.Threading.Tasks;
using Avalonia.Platform.Storage;

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
        // Thumbnail
        // Accessing underlying property if possible or trigger load
        // Assuming originalImage has logic to provide bitmap or we load it ourselves
        if (System.IO.File.Exists(_originalImage.ImagePath))
        {
             var info = new FileInfo(_originalImage.ImagePath);
             OriginalFileSize = $"Size: {FormatFileSize(info.Length)}";
             OriginalDate = $"Date: {info.CreationTime.ToShortDateString()} {info.CreationTime.ToShortTimeString()}";
             
             if (_originalImage.IsImage)
             {
                 try
                 {
                     // Load bitmap for resolution and display
                     using var stream = System.IO.File.OpenRead(_originalImage.ImagePath);
                     var bitmap = Bitmap.DecodeToWidth(stream, 400); // Decode smaller for UI
                     OriginalThumbnail = bitmap;
                     OriginalResolution = $"Resolution: {bitmap.Size.Width} x {bitmap.Size.Height}";
                 }
                 catch { OriginalResolution = "Resolution: Unknown"; }
             }
             else if (_originalImage.IsVideo)
             {
                 // For video, we might use the thumbnail path if generated
                 if (!string.IsNullOrEmpty(_originalImage.ThumbnailPath) && File.Exists(_originalImage.ThumbnailPath))
                 {
                      using var stream = System.IO.File.OpenRead(_originalImage.ThumbnailPath);
                      OriginalThumbnail = new Bitmap(stream);
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
                 using var stream = File.OpenRead(filePath);
                 var bitmap = Bitmap.DecodeToWidth(stream, 400);
                 NewThumbnail = bitmap;
                 NewResolution = $"Resolution: {bitmap.Size.Width} x {bitmap.Size.Height}";
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
