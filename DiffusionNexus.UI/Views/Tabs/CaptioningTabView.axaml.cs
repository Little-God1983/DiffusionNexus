using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DiffusionNexus.UI.ViewModels.Tabs;

namespace DiffusionNexus.UI.Views.Tabs;

public partial class CaptioningTabView : UserControl
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".bmp"];

    public CaptioningTabView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);

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

        var files = e.Data.GetFiles();
        if (files is null) return;

        foreach (var item in files)
        {
            if (item is IStorageFile file)
            {
                var path = file.Path.LocalPath;
                if (IsImageFile(path) && DataContext is CaptioningTabViewModel vm)
                {
                    vm.LoadSingleImage(path);
                    return;
                }
            }
        }
    }

    private bool HasImageFiles(DragEventArgs e)
    {
        var files = e.Data.GetFiles();
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
