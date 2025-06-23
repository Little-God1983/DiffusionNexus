using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.UI.Classes;
using DiffusionNexus.UI.Models;
using ReactiveUI;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Threading.Tasks;

namespace DiffusionNexus.UI.ViewModels
{
    public partial class PromptEditorControlViewModel : ObservableObject
    {
        public IDialogService DialogService { get; set; }
        public PromptProfileService PromptProfileService { get; set; } = new PromptProfileService();

        [ObservableProperty]
        private ObservableCollection<PromptProfileModel>? _profiles;

        [ObservableProperty]
        private PromptProfileModel? _selectedProfile;

        [ObservableProperty]
        private string? _blacklist;

        [ObservableProperty]
        private string? _whitelist;
        public string Prompt { get; set; }
        public string NegativePrompt { get; set; }
        public ReactiveCommand<Unit, Unit> AskForTextCommand { get; }

        public ReactiveCommand<Unit, Unit> SaveProfileCommand { get; }
        public ReactiveCommand<Unit, Unit> LoadProfileCommand { get; }
        public ReactiveCommand<Unit, Unit> ClearCommand { get; }
        public ReactiveCommand<Unit, Unit> DeleteProfileCommand { get; }


        public PromptEditorControlViewModel()
        {
            SaveProfileCommand = ReactiveCommand.CreateFromTask(SavePrompt);
            ClearCommand = ReactiveCommand.Create(ClearPrompt);
            DeleteProfileCommand = ReactiveCommand.Create(() =>
            {
                if (SelectedProfile != null)
                {
                    Profiles.Remove(SelectedProfile);
                    SelectedProfile = null;
                }
            });

            AskForTextCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                // this knows nothing about Windows…
                var result = await DialogService.ShowInputAsync("Enter something:");
                if (result != null)
                {
                    /* …use it… */
                }
            });

            _ = LoadProfilesAsync();
        }

        private async Task SavePrompt()
        {
            var name = SelectedProfile?.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = await DialogService.ShowInputAsync("Enter name for new profile");
                if (string.IsNullOrWhiteSpace(name))
                    return;
            }
            else if (await PromptProfileService.ExistsByNameAsync(name))
            {
                var confirm = await DialogService.ShowConfirmationAsync($"Profile '{name}' already exists. Overwrite?", true);
                if (confirm == false)
                {
                    name = await DialogService.ShowInputAsync("Enter name for new profile");
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
            await PromptProfileService.SaveAsync(profile);
            SelectedProfile = profile;
            await LoadProfilesAsync();
        }

        private void ClearPrompt() { Prompt = string.Empty; NegativePrompt = string.Empty; }


        private async Task LoadProfilesAsync()
        {
            var list = await PromptProfileService.LoadAllAsync();

            Profiles ??= new ObservableCollection<PromptProfileModel>();
            Profiles.Clear();
            foreach (var p in list)
                Profiles.Add(p);
            if (SelectedProfile == null && Profiles.Count > 0)
            {
                SelectedProfile = Profiles[0];
            }
        }

        partial void OnSelectedProfileChanged(PromptProfileModel? value)
        {
            if (value != null)
                _ = LoadSelectedProfileAsync(value);
        }

        private async Task LoadSelectedProfileAsync(PromptProfileModel profile)
        {
            var PromptProfileModel = await PromptProfileService.GetProfileAsync(profile);
            if (profile != null)
            {
                Blacklist = profile.Blacklist;
                Whitelist = profile.Whitelist;
            }
        }
    }
}

