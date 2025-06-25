using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace DiffusionNexus.UI.ViewModels
{
    public partial class LoraSortCustomMappingsViewModel : ViewModelBase
    {
        [ObservableProperty]
        private ObservableCollection<CustomTagMapping> customTagMappings = new();
        [ObservableProperty]
        private bool isCustomEnabled = true;

        public IRelayCommand MoveUpCommand { get; }
        public IRelayCommand MoveDownCommand { get; }
        public IRelayCommand AddMappingCommand { get; }
        public IRelayCommand RemoveMappingCommand { get; }
        public IRelayCommand SaveAllMappingsCommand { get; }
        public IRelayCommand DeleteAllMappingsCommand { get; }

        public LoraSortCustomMappingsViewModel()
        {
            MoveUpCommand = new RelayCommand(() => { });
            MoveDownCommand = new RelayCommand(() => { });
            AddMappingCommand = new RelayCommand(() => { });
            RemoveMappingCommand = new RelayCommand(() => { });
            SaveAllMappingsCommand = new RelayCommand(() => { });
            DeleteAllMappingsCommand = new RelayCommand(() => { });
        }
    }

    public class CustomTagMapping
    {
        public string? LookForTag { get; set; }
        public string? MapToFolder { get; set; }
    }
}
