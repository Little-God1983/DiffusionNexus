using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DiffusionNexus.UI.ViewModels.Tabs;

namespace DiffusionNexus.UI.Views.Tabs;

/// <summary>
/// View for the Auto Scale/Crop tab. Handles folder browsing functionality.
/// </summary>
public partial class AutoScaleCropView : UserControl
{
    public AutoScaleCropView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

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

        if (folders.Count > 0 && DataContext is AutoScaleCropTabViewModel vm)
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

        if (folders.Count > 0 && DataContext is AutoScaleCropTabViewModel vm)
        {
            vm.TargetFolder = folders[0].Path.LocalPath;
        }
    }
}
