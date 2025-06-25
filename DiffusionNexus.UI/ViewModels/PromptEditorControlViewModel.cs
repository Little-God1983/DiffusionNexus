using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.UI.Classes;
using DiffusionNexus.UI.Models;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;

namespace DiffusionNexus.UI.ViewModels
{
    public partial class PromptEditorControlViewModel : ViewModelBase
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

        [ObservableProperty]
        private string? _prompt;
        [ObservableProperty]
        private string? _negativePrompt;
        public IAsyncRelayCommand AskForTextCommand { get; }

        public IAsyncRelayCommand SaveProfileCommand { get; }
        public IAsyncRelayCommand LoadProfileCommand { get; }
        public IRelayCommand ClearCommand { get; }
        public IRelayCommand DeleteProfileCommand { get; }


        public PromptEditorControlViewModel()
        {
            SaveProfileCommand = new AsyncRelayCommand(SavePrompt);
            ClearCommand = new RelayCommand(ClearPrompt);
            DeleteProfileCommand = new RelayCommand(() =>
            {
                if (SelectedProfile != null)
                {
                    Profiles.Remove(SelectedProfile);
                    SelectedProfile = null;
                }
            });

            AskForTextCommand = new AsyncRelayCommand(async () =>
            {
                var result = await DialogService.ShowInputAsync("Enter something:");
                if (result != null)
                {
                    // use result
                }
            });

            LoadProfileCommand = new AsyncRelayCommand(LoadProfilesAsync);

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

