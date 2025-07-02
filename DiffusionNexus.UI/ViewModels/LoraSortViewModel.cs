using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffusionNexus.UI.ViewModels
{
    public partial class LoraSortViewModel : ObservableObject
    {
        public LoraSortMainSettingsViewModel MainSettingsViewModel { get; }
        public LoraSortCustomMappingsViewModel CustomMappingsViewModel { get; }

        public LoraSortViewModel()
        {
            MainSettingsViewModel = new LoraSortMainSettingsViewModel();
            CustomMappingsViewModel = new LoraSortCustomMappingsViewModel();

            // Subscribe to property changes in the main settings
            MainSettingsViewModel.PropertyChanged += OnMainSettingsPropertyChanged;
            
            // Initialize the custom mappings enabled state
            CustomMappingsViewModel.IsCustomEnabled = MainSettingsViewModel.UseCustomMappings;
        }

        private void OnMainSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LoraSortMainSettingsViewModel.UseCustomMappings))
            {
                CustomMappingsViewModel.IsCustomEnabled = MainSettingsViewModel.UseCustomMappings;
            }
        }
    }
}
