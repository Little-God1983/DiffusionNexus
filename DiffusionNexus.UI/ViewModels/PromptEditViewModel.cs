using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.UI.Classes;
using DiffusionNexus.UI.Models;

namespace DiffusionNexus.UI.ViewModels
{
    public partial class PromptEditViewModel : ObservableObject
    {
        private readonly PromptProfileService _service;

        [ObservableProperty]
        private ObservableCollection<PromptProfileModel>? profiles = new();

        [ObservableProperty]
        private string? _selectedProfile;

        [ObservableProperty]
        private string? _blacklist;

        [ObservableProperty]
        private string? _whitelist;

        public PromptEditViewModel() : this(new PromptProfileService())
        {
        }

        public PromptEditViewModel(PromptProfileService service)
        {
            _service = service;
            _ = LoadProfilesAsync();
        }

        private async Task LoadProfilesAsync()
        {
            var list = await _service.LoadAllAsync();
            Profiles = new ObservableCollection<PromptProfileModel>(list);
        }

        partial void OnSelectedProfileChanged(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                _ = LoadSelectedProfileAsync(value);
        }

        private async Task LoadSelectedProfileAsync(string name)
        {
            //var profile = await _service.GetProfileAsync(name);
            //if (profile != null)
            //{
            //    Blacklist = profile.Blacklist;
            //    Whitelist = profile.Whitelist;
            //}
        }

        public async Task SaveProfileAsync(IDialogService dialog)
        {
            var name = SelectedProfile;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = await dialog.ShowInputAsync("Enter name for new profile");
                if (string.IsNullOrWhiteSpace(name))
                    return;
            }
            else if (await _service.ExistsByNameAsync(name))
            {
                var confirm = await dialog.ShowConfirmationAsync($"Profile '{name}' already exists. Overwrite?", true);
                if (confirm == false)
                {
                    name = await dialog.ShowInputAsync("Enter name for new profile");
                    if (string.IsNullOrWhiteSpace(name))
                        return;
                }
                else if (confirm == null)
                {
                    return;
                }
            }

            var profile = new PromptProfileModel
            {
                Name = name,
                Blacklist = Blacklist ?? string.Empty,
                Whitelist = Whitelist ?? string.Empty
            };
            await _service.SaveAsync(profile);
            SelectedProfile = name;
            await LoadProfilesAsync();
        }

        public async Task DeleteProfileAsync(IDialogService dialog)
        {
            //if (string.IsNullOrWhiteSpace(SelectedProfile))
            //    return;

            //var confirm = await dialog.ShowConfirmationAsync($"Do you really want to delete profile '{SelectedProfile.Name}'?");
            //if (confirm != true)
            //    return;

            //await _service.DeleteAsync(SelectedProfile);
            //SelectedProfile = null;
            //await LoadProfilesAsync();
        }
    }
}
