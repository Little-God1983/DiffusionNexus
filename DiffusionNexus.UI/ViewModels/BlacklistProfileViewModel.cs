using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.Classes;
using DiffusionNexus.UI.Models;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace DiffusionNexus.UI.ViewModels
{
    public partial class BlacklistProfileViewModel : ObservableObject
    {
        private readonly PromptProfileService _service;

        [ObservableProperty]
        private ObservableCollection<string> _profiles = new();

        [ObservableProperty]
        private string? _selectedProfile;

        [ObservableProperty]
        private string? _blacklist;

        [ObservableProperty]
        private string? _whitelist;
        public IAsyncRelayCommand<Window?> SaveProfileCommand { get; }
        public IAsyncRelayCommand<Window?> DeleteProfileCommand { get; }
        public IRelayCommand ApplyListCommand { get; }
        public IRelayCommand SaveCommand { get; }
        public IRelayCommand SaveAsCommand { get; }


        public BlacklistProfileViewModel() : this(new PromptProfileService())
        {
        }

        public BlacklistProfileViewModel(PromptProfileService service)
        {
            _service = service;
            _ = LoadProfilesAsync();

            // wire them up to your existing methods
            SaveProfileCommand = new AsyncRelayCommand<Window?>(async w => {
                if (w is null) return;
                var dlg = new DialogService(w);
                await SaveProfileAsync(dlg);
            });

            DeleteProfileCommand = new AsyncRelayCommand<Window?>(async w => {
                if (w is null) return;
                var dlg = new DialogService(w);
                await DeleteProfileAsync(dlg);
            });

            ApplyListCommand = new RelayCommand(OnApplyList);
            SaveCommand = new RelayCommand(OnSave);
            SaveAsCommand = new RelayCommand(OnSaveAs);
        }

        private async Task LoadProfilesAsync()
        {
            var list = await _service.LoadAllAsync();
            Profiles = new ObservableCollection<string>(list.Select(p => p.Name));
        }

        partial void OnSelectedProfileChanged(string? value)
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
            var name = SelectedProfile;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = await dialog.ShowInputAsync("Enter name for new profile");
                if (string.IsNullOrWhiteSpace(name))
                    return;
            }
            else if (await _service.ExistsAsync(name))
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
            if (string.IsNullOrWhiteSpace(SelectedProfile))
                return;

            var confirm = await dialog.ShowConfirmationAsync($"Do you really want to delete profile '{SelectedProfile}'?");
            if (confirm != true)
                return;

            await _service.DeleteAsync(SelectedProfile);
            SelectedProfile = null;
            await LoadProfilesAsync();
        }

        private void OnSave()
        {
            // your existing “Save” logic (parameters into the image)…
        }
        private void OnApplyList()
        {
            // whatever your old ApplyListClicked handler did…
        }
        private void OnSaveAs()
        {
            // your existing “Save As” logic…
        }
    }
}
