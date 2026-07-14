using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using DiffusionNexus.UI.ViewModels.Pipelines;

namespace DiffusionNexus.UI.Views.Pipelines;

public partial class BatchMetadataDistillerView : UserControl
{
    public BatchMetadataDistillerView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnBrowseOutputFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BatchMetadataDistillerViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose output folder",
            AllowMultiple = false,
        });

        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) vm.OutputFolder = path;
    }
}
