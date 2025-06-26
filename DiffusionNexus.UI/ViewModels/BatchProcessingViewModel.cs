using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Linq;
using System.Threading.Tasks;

namespace DiffusionNexus.UI.ViewModels
{
    public partial class BatchProcessingViewModel : ViewModelBase
    {
        [ObservableProperty]
        private string? _sourceFolder;

        [ObservableProperty]
        private string? _targetFolder;

        [ObservableProperty]
        private bool _useListOnly = true;

        [ObservableProperty]
        private bool _replaceAll;

        public IAsyncRelayCommand<Window?> BrowseSourceFolderCommand { get; }
        public IAsyncRelayCommand<Window?> BrowseTargetFolderCommand { get; }

        public BatchProcessingViewModel()
        {
            BrowseSourceFolderCommand = new AsyncRelayCommand<Window?>(OnBrowseSourceFolder);
            BrowseTargetFolderCommand = new AsyncRelayCommand<Window?>(OnBrowseTargetFolder);
            OnUseListOnlyChanged(true);
        }

        partial void OnUseListOnlyChanged(bool value)
        {
            if (value)
                ReplaceAll = false;
        }

        partial void OnReplaceAllChanged(bool value)
        {
            if (value)
                UseListOnly = false;
        }

        private async Task OnBrowseSourceFolder(Window? window)
        {
            if (window is null) return;

            var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions());
            var path = folders.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
            {
                SourceFolder = path;
            }
        }

        private async Task OnBrowseTargetFolder(Window? window)
        {
            if (window is null) return;

            var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions());
            var path = folders.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
            {
                TargetFolder = path;
            }
        }

    }
}
