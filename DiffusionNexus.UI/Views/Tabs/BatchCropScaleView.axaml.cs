using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DiffusionNexus.UI.ViewModels.Tabs;

namespace DiffusionNexus.UI.Views.Tabs;

/// <summary>
/// View for the Batch Crop/Scale tab. Handles folder browsing and single-image drag-drop.
/// </summary>
public partial class BatchCropScaleView : UserControl
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".bmp"];
    private bool _eventsWired;

    public BatchCropScaleView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // Only wire up events once to prevent multiple folder picker dialogs
        if (_eventsWired) return;
        _eventsWired = true;

        // Wire up browse buttons
        var browseSourceButton = this.FindControl<Button>("BrowseSourceButton");
        var browseTargetButton = this.FindControl<Button>("BrowseTargetButton");

        if (browseSourceButton != null)
        {
            browseSourceButton.Click += OnBrowseSourceClick;
        }

        if (browseTargetButton != null)
        {
            browseTargetButton.Click += OnBrowseTargetClick;
        }

        // Wire up single-image drag-drop zones
        var dropZone = this.FindControl<Border>("SingleImageDropZone");
        if (dropZone is not null)
        {
            DragDrop.SetAllowDrop(dropZone, true);
            dropZone.AddHandler(DragDrop.DragOverEvent, OnSingleImageDragOver);
            dropZone.AddHandler(DragDrop.DragLeaveEvent, OnSingleImageDragLeave);
            dropZone.AddHandler(DragDrop.DropEvent, OnSingleImageDrop);
        }

        var loadedZone = this.FindControl<Border>("SingleImageLoadedZone");
        if (loadedZone is not null)
        {
            DragDrop.SetAllowDrop(loadedZone, true);
            loadedZone.AddHandler(DragDrop.DragOverEvent, OnSingleImageDragOver);
            loadedZone.AddHandler(DragDrop.DragLeaveEvent, OnSingleImageDragLeave);
            loadedZone.AddHandler(DragDrop.DropEvent, OnSingleImageDrop);
        }
    }

    private async void OnBrowseSourceClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Source Folder",
            AllowMultiple = false
        });

        if (folders.Count > 0 && DataContext is BatchCropScaleTabViewModel vm)
        {
            vm.SourceFolder = folders[0].Path.LocalPath;
        }
    }

    private async void OnBrowseTargetClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Target Folder (Optional)",
            AllowMultiple = false
        });

        if (folders.Count > 0 && DataContext is BatchCropScaleTabViewModel vm)
        {
            vm.TargetFolder = folders[0].Path.LocalPath;
        }
    }

    private void OnSingleImageDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = HasImageFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;

        if (sender is Border border && e.DragEffects == DragDropEffects.Copy)
        {
            border.BorderBrush = new SolidColorBrush(Color.Parse("#4CAF50"));
            border.BorderThickness = new Avalonia.Thickness(2);
        }
    }

    private void OnSingleImageDragLeave(object? sender, DragEventArgs e)
    {
        if (sender is Border border)
        {
            border.BorderBrush = new SolidColorBrush(Color.Parse("#444"));
            border.BorderThickness = new Avalonia.Thickness(2);
        }
    }

    private void OnSingleImageDrop(object? sender, DragEventArgs e)
    {
        if (sender is Border border)
        {
            border.BorderBrush = new SolidColorBrush(Color.Parse("#444"));
            border.BorderThickness = new Avalonia.Thickness(2);
        }

        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return;

        foreach (var item in files)
        {
            if (item is IStorageFile file)
            {
                var path = file.Path.LocalPath;
                if (IsImageFile(path) && DataContext is BatchCropScaleTabViewModel vm)
                {
                    vm.LoadSingleImage(path);
                    return;
                }
            }
        }
    }

    private bool HasImageFiles(DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return false;

        foreach (var item in files)
        {
            if (item is IStorageFile file && IsImageFile(file.Path.LocalPath))
                return true;
        }
        return false;
    }

    private static bool IsImageFile(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        return ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }
}
