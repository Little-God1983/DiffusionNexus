using DiffusionNexus.UI.Classes;
using DiffusionNexus.UI.Models;
using ReactiveUI;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;

namespace DiffusionNexus.UI.ViewModels
{
    public class PromptEditorControlViewModel : ReactiveObject
    {
        public ObservableCollection<PromptProfileModel> Profiles { get; } = new ObservableCollection<PromptProfileModel>();
        public PromptProfileModel SelectedProfile { get; set; }
        public string Blacklist { get; set; }
        public string Whitelist { get; set; }
        public string Prompt { get; set; }
        public string NegativePrompt { get; set; }

        public ReactiveCommand<Unit, Unit> SaveCommand { get; }
        public ReactiveCommand<Unit, Unit> LoadCommand { get; }
        public ReactiveCommand<Unit, Unit> ClearCommand { get; }
        public ReactiveCommand<Unit, Unit> DeleteCommand { get; }
        private readonly PromptProfileService _service;


        public PromptEditorControlViewModel()
        {
            SaveCommand = ReactiveCommand.Create(SavePrompt);
            LoadCommand = ReactiveCommand.Create(LoadPrompt);
            ClearCommand = ReactiveCommand.Create(ClearPrompt);
            DeleteCommand = ReactiveCommand.Create(() =>
            {
                if (SelectedProfile != null)
                {
                    Profiles.Remove(SelectedProfile);
                    SelectedProfile = null;
                }
            });
        }

        private void SavePrompt() { /* … */ }
        private void LoadPrompt() { /* … */ }
        private void ClearPrompt() { Prompt = string.Empty; NegativePrompt = string.Empty; }


        private async Task LoadProfilesAsync()
        {
            var list = await _service.LoadAllAsync();
        }

        void OnSelectedProfileChanged(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                _ = LoadSelectedProfileAsync(value);
        }

        private async Task LoadSelectedProfileAsync(string name)
        {
            var profile = await _service.GetAsync(name);
            if (profile != null)
            {
                Blacklist = profile.Blacklist;
                Whitelist = profile.Whitelist;
            }
        }

        public async Task SaveProfileAsync(IDialogService dialog)
        {
            var name = SelectedProfile.Name;
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
            SelectedProfile = profile;
            await LoadProfilesAsync();
        }

        public async Task DeleteProfileAsync(IDialogService dialog)
        {
            if (string.IsNullOrWhiteSpace(SelectedProfile.Name))
                return;

            var confirm = await dialog.ShowConfirmationAsync($"Do you really want to delete profile '{SelectedProfile}'?");
            if (confirm != true)
                return;

            await _service.DeleteAsync(SelectedProfile);
            SelectedProfile = null;
            await LoadProfilesAsync();
        }

    }
}

