using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffusionNexus.UI.ViewModels
{
    public partial class BatchPromptEditViewModel : ObservableObject
    {
        [ObservableProperty]
        private string? _sourceFolder;

        [ObservableProperty]
        private string? _targetFolder;

        [ObservableProperty]
        private bool _useListOnly = true;

        [ObservableProperty]
        private bool _replaceAll;

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
    }
}
