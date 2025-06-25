using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Threading.Tasks;

namespace DiffusionNexus.UI.ViewModels
{
    public partial class LoraSortMainSettingsViewModel : ViewModelBase
    {
        [ObservableProperty]
        private string? basePath;
        [ObservableProperty]
        private string? targetPath;
        [ObservableProperty]
        private bool isCopyMode = true;
        [ObservableProperty]
        private bool overrideFiles;
        [ObservableProperty]
        private bool createBaseFolders = true;
        [ObservableProperty]
        private bool useCustomMappings;
        [ObservableProperty]
        private double progress;
        [ObservableProperty]
        private string? statusText;
        [ObservableProperty]
        private string actionButtonText = "Go";

        public IRelayCommand SelectBasePathCommand { get; }
        public IRelayCommand SelectTargetPathCommand { get; }
        public IRelayCommand GoCommand { get; }

        public LoraSortMainSettingsViewModel()
        {
            SelectBasePathCommand = new RelayCommand(OnSelectBasePath);
            SelectTargetPathCommand = new RelayCommand(OnSelectTargetPath);
            GoCommand = new RelayCommand(OnGo);
        }

        private void OnSelectBasePath()
        {
            // TODO: Implement folder picker logic
            StatusText = "SelectBasePath clicked (not implemented)";
        }

        private void OnSelectTargetPath()
        {
            // TODO: Implement folder picker logic
            StatusText = "SelectTargetPath clicked (not implemented)";
        }

        private void OnGo()
        {
            // TODO: Implement main action logic
            StatusText = "Go clicked (not implemented)";
            Progress = 0;
        }
    }
}
