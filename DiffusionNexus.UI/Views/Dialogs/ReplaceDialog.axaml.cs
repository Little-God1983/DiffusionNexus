using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Dialogs;

public partial class ReplaceDialog : Window, IDialogCloseable
{
    public ReplaceDialog()
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

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Replacement File",
            AllowMultiple = false,
            FileTypeFilter = new[] 
            { 
               new FilePickerFileType("Media Files") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.mp4", "*.webm", "*.mov" } },
               FilePickerFileTypes.All 
            }
        });

        if (files.Count > 0 && DataContext is ReplaceImageDialogViewModel vm)
        {
            await vm.SetNewFileAsync(files[0].Path.LocalPath);
        }
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
    }

    private void OnDragLeave(object? sender, RoutedEventArgs e)
    {
        // visual cleanup if needed
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            var files = e.DataTransfer.TryGetFiles();
            var firstFile = files?.FirstOrDefault()?.Path.LocalPath;
            
            if (firstFile != null && DataContext is ReplaceImageDialogViewModel vm)
            {
                await vm.SetNewFileAsync(firstFile);
            }
        }
    }
}
