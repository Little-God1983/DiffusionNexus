using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.UI.Classes;
using DiffusionNexus.UI.Models;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using DiffusionNexus.Service.Classes;

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
        public IRelayCommand NewProfileCommand { get; }
        public IAsyncRelayCommand DeleteProfileCommand { get; }

        public IAsyncRelayCommand CopyPromptCommand { get; }
        public IAsyncRelayCommand CopyNegativePromptCommand { get; }

        public PromptEditorControlViewModel()
        {
            SaveProfileCommand = new AsyncRelayCommand(SavePrompt);
            ClearCommand = new RelayCommand(ClearPrompt);
            NewProfileCommand = new RelayCommand(NewProfile);
            DeleteProfileCommand = new AsyncRelayCommand(DeleteProfileAsync);
            CopyPromptCommand = new AsyncRelayCommand(CopyPromptAsync);
            CopyNegativePromptCommand = new AsyncRelayCommand(CopyNegativePromptAsync);

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
            string? name = SelectedProfile?.Name;

            if (SelectedProfile != null &&
                SelectedProfile.Blacklist == (Blacklist ?? string.Empty) &&
                SelectedProfile.Whitelist == (Whitelist ?? string.Empty))
            {
                var copy = await DialogService.ShowConfirmationAsync("No new values detected - Do you want to create a copy?", false);
                if (copy != true)
                {
                    Log("save cancelled", LogSeverity.Error);
                    return;
                }
                name = null; // force new name
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                name = await DialogService.ShowInputAsync("Enter name for new profile");
                if (string.IsNullOrWhiteSpace(name))
                {
                    Log("profile name required", LogSeverity.Error);
                    return;
                }
            }
            else if (await PromptProfileService.ExistsByNameAsync(name))
            {
                var confirm = await DialogService.ShowConfirmationAsync($"Profile '{name}' already exists. Overwrite?", true);
                if (confirm == false)
                {
                    name = await DialogService.ShowInputAsync("Enter name for new profile");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        Log("profile name required", LogSeverity.Error);
                        return;
                    }
                }
                else if (confirm == null)
                {
                    Log("save cancelled", LogSeverity.Error);
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
            await LoadProfilesAsync();
            SelectedProfile = Profiles.FirstOrDefault(p => p.Name == name);
            Log($"profile '{name}' saved", LogSeverity.Success);
        }

        private void ClearPrompt() { Prompt = string.Empty; NegativePrompt = string.Empty; }

        private void NewProfile()
        {
            Profiles ??= new ObservableCollection<PromptProfileModel>();
            var profile = Profiles.FirstOrDefault(x => String.IsNullOrEmpty(x.Name));
            if (profile == null)
            {
                profile = new PromptProfileModel();
                Profiles.Add(profile);
                SelectedProfile = profile;
            }
            else
            {
                SelectedProfile = profile;
            }
            Blacklist = string.Empty;
            Whitelist = string.Empty;
            Log("new profile created", LogSeverity.Success);
        }

        private async Task DeleteProfileAsync()
        {
            if (SelectedProfile == null)
            {
                Log("no profile selected", LogSeverity.Error);
                return;
            }

            var confirm = await DialogService.ShowConfirmationAsync($"Do you want to delete '{SelectedProfile.Name}'?", false);
            if (confirm != true)
            {
                Log("delete cancelled", LogSeverity.Error);
                return;
            }

            await PromptProfileService.DeleteAsync(SelectedProfile);
            await LoadProfilesAsync();
            SelectedProfile = Profiles.FirstOrDefault();
            if (SelectedProfile == null)
            {
                Blacklist = string.Empty;
                Whitelist = string.Empty;
                Prompt = string.Empty;
                NegativePrompt = string.Empty;
            }
            Log("profile deleted", LogSeverity.Success);
        }

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

        private async Task CopyPromptAsync()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow is { Clipboard: { } clipboard })
            {
                if (String.IsNullOrEmpty(Prompt))
                {
                    Log("no prompt to copy", LogSeverity.Warning);
                    return;
                }
                try
                {
                    await clipboard.SetTextAsync(Prompt ?? string.Empty);
                    Log("prompt copied to clipboard", LogSeverity.Success);
                }
                catch (Exception ex)
                {
                    Log($"failed to copy prompt: {ex.Message}", LogSeverity.Error);
                }
            }
        }

        private async Task CopyNegativePromptAsync()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow is { Clipboard: { } clipboard })
            {
                if(String.IsNullOrEmpty(NegativePrompt))
                {
                    Log("no negative prompt to copy", LogSeverity.Warning);
                    return;
                }
                try
                {
                    await clipboard.SetTextAsync(NegativePrompt ?? string.Empty);
                    Log("negative prompt copied to clipboard", LogSeverity.Success);
                }
                catch (Exception ex)
                {
                    Log($"failed to copy negative prompt: {ex.Message}", LogSeverity.Error);
                }
            }
        }
    }
}

