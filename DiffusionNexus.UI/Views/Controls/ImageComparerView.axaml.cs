using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using DiffusionNexus.UI.Utilities;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Controls;

/// <summary>
/// Image comparison control with drag-and-drop support.
/// </summary>
public partial class ImageComparerView : UserControl
{
    public ImageComparerView()
    {
        InitializeComponent();

        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (DataContext is not ImageComparerViewModel viewModel)
        {
            return;
        }

        var hasImages = HasImageFiles(e);
        viewModel.IsDragOver = hasImages;
        e.DragEffects = hasImages ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (DataContext is ImageComparerViewModel viewModel)
        {
            viewModel.IsDragOver = false;
        }
    }

#pragma warning disable CS0618 // Data property is still required for GetFiles extension
    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not ImageComparerViewModel viewModel)
        {
            return;
        }

        viewModel.IsDragOver = false;

        var files = e.Data.GetFiles();
        if (files is null)
        {
            return;
        }

        var imagePaths = new List<string>();

        foreach (var item in files)
        {
            if (item is IStorageFile file)
            {
                var path = file.Path.LocalPath;
                if (MediaFileExtensions.IsImageFile(path))
                {
                    imagePaths.Add(path);
                }
            }
        }

        if (imagePaths.Count > 0)
        {
            viewModel.HandleDroppedFiles(imagePaths);
        }
    }
#pragma warning restore CS0618

#pragma warning disable CS0618 // Data property is still required for GetFiles extension
    private static bool HasImageFiles(DragEventArgs e)
    {
        var files = e.Data.GetFiles();
        if (files is null)
        {
            return false;
        }

        foreach (var item in files)
        {
            if (item is IStorageFile file && MediaFileExtensions.IsImageFile(file.Path.LocalPath))
            {
                return true;
            }
        }

        return false;
    }
#pragma warning restore CS0618
}
