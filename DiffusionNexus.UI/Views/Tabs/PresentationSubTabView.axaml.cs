using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.UI.ViewModels.Tabs;

namespace DiffusionNexus.UI.Views.Tabs;

/// <summary>
/// View for the Presentation sub-tab within dataset version detail view.
/// Displays media gallery and document list for showcasing trained models.
/// Handles drag-and-drop of media and document files.
/// </summary>
public partial class PresentationSubTabView : UserControl
{
    public PresentationSubTabView()
    {
        InitializeComponent();
        
        // Set up drag-drop handlers
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
        var dropZone = this.FindControl<Border>("PresentationDropZone");
        if (dropZone is null) return;

        var hasValidFiles = AnalyzeFilesInDrag(e);
        
        if (hasValidFiles)
        {
            dropZone.BorderBrush = Avalonia.Media.Brushes.LimeGreen;
            dropZone.BorderThickness = new Avalonia.Thickness(3);
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            dropZone.BorderBrush = Avalonia.Media.Brushes.Red;
            dropZone.BorderThickness = new Avalonia.Thickness(3);
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        var dropZone = this.FindControl<Border>("PresentationDropZone");
        if (dropZone is null) return;
        
        dropZone.BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#444"));
        dropZone.BorderThickness = new Avalonia.Thickness(3);
    }

#pragma warning disable CS0618 // Type or member is obsolete - Data property is still required for GetFiles extension
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        OnDragLeave(sender, e);

        if (DataContext is not PresentationTabViewModel viewModel) return;

        var files = e.Data.GetFiles();
        if (files is null) return;

        var validFiles = new List<string>();

        foreach (var item in files)
        {
            if (item is IStorageFile file)
            {
                var filePath = file.Path.LocalPath;
                if (PresentationFileItem.IsSupportedFile(filePath))
                {
                    validFiles.Add(filePath);
                }
            }
            else if (item is IStorageFolder folder)
            {
                // Recursively find supported files in folder
                AddFilesFromFolder(folder.Path.LocalPath, validFiles);
            }
        }

        if (validFiles.Count > 0)
        {
            await viewModel.AddFilesAsync(validFiles);
        }
    }

    private static bool AnalyzeFilesInDrag(DragEventArgs e)
    {
        var files = e.Data.GetFiles();
        if (files is null) return false;

        foreach (var item in files)
        {
            if (item is IStorageFile file)
            {
                if (PresentationFileItem.IsSupportedFile(file.Path.LocalPath))
                {
                    return true;
                }
            }
            else if (item is IStorageFolder folder)
            {
                if (HasSupportedFilesInFolder(folder.Path.LocalPath))
                {
                    return true;
                }
            }
        }

        return false;
    }
#pragma warning restore CS0618

    private static bool HasSupportedFilesInFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return false;

        try
        {
            return Directory.EnumerateFiles(folderPath)
                .Any(f => PresentationFileItem.IsSupportedFile(f));
        }
        catch
        {
            return false;
        }
    }

    private static void AddFilesFromFolder(string folderPath, List<string> validFiles)
    {
        if (!Directory.Exists(folderPath)) return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(folderPath))
            {
                if (PresentationFileItem.IsSupportedFile(file))
                {
                    validFiles.Add(file);
                }
            }
        }
        catch
        {
            // Ignore access errors
        }
    }
}
