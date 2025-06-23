using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DiffusionNexus.UI.ViewModels
{
    public partial class BatchProcessingViewModel : ObservableObject
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
        }

        //partial void OnUseListOnlyChanged(bool value)
        //{
        //    if (value)
        //        ReplaceAll = false;
        //}

        //partial void OnReplaceAllChanged(bool value)
        //{
        //    if (value)
        //        UseListOnly = false;
        //}

        private async Task OnBrowseSourceFolder(Window? window)
        {
            if (window is null) return;
            var dlg = new OpenFolderDialog();
            var result = await dlg.ShowAsync(window);
            if (!string.IsNullOrEmpty(result))
            {
                SourceFolder = result;
            }
        }

        private async Task OnBrowseTargetFolder(Window? window)
        {
            if (window is null) return;
            var dlg = new OpenFolderDialog();
            var result = await dlg.ShowAsync(window);
            if (!string.IsNullOrEmpty(result))
            {
                TargetFolder = result;
            }
        }

    }
}
