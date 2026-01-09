using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.UI.ViewModels.Tabs;

namespace DiffusionNexus.UI.Views.Tabs;

/// <summary>
/// View for the Epochs sub-tab within dataset version detail view.
/// Handles drag-and-drop of epoch/checkpoint files.
/// </summary>
public partial class EpochsSubTabView : UserControl
{
    public EpochsSubTabView()
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
        var dropZone = this.FindControl<Border>("EpochsDropZone");
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
        var dropZone = this.FindControl<Border>("EpochsDropZone");
        if (dropZone is null) return;
        
        dropZone.BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#444"));
        dropZone.BorderThickness = new Avalonia.Thickness(3);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        OnDragLeave(sender, e);

        if (DataContext is not EpochsTabViewModel viewModel) return;

        var files = e.Data.GetFiles();
        if (files is null) return;

        var validFiles = new List<string>();

        foreach (var item in files)
        {
            if (item is IStorageFile file)
            {
                var filePath = file.Path.LocalPath;
                if (EpochFileItem.IsEpochFile(filePath))
                {
                    validFiles.Add(filePath);
                }
            }
            else if (item is IStorageFolder folder)
            {
                // Recursively find epoch files in folder
                AddEpochFilesFromFolder(folder.Path.LocalPath, validFiles);
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
                if (EpochFileItem.IsEpochFile(file.Path.LocalPath))
                {
                    return true;
                }
            }
            else if (item is IStorageFolder folder)
            {
                if (HasEpochFilesInFolder(folder.Path.LocalPath))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasEpochFilesInFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return false;

        try
        {
            return Directory.EnumerateFiles(folderPath)
                .Any(f => EpochFileItem.IsEpochFile(f));
        }
        catch
        {
            return false;
        }
    }

    private static void AddEpochFilesFromFolder(string folderPath, List<string> validFiles)
    {
        if (!Directory.Exists(folderPath)) return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(folderPath))
            {
                if (EpochFileItem.IsEpochFile(file))
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
