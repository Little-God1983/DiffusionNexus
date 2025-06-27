using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.Views;
using DiffusionNexus.UI.Classes;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.ViewModels
{
    public class MainWindowViewModel : ViewModelBase
    {
        public ObservableCollection<ModuleItem> Modules { get; } = new ObservableCollection<ModuleItem>
        {
            new("Lora Sort", "avares://DiffusionNexus.UI/Assets/LoraSort.png", new LoraSortView()),
            new("Prompt Edit", "avares://DiffusionNexus.UI/Assets/PromptEdit.png", new PromptEditView()),
            new("Lora Helper", "avares://DiffusionNexus.UI/Assets/HumanCogwheel.png", new LoraHelperView())
        };

        public IRelayCommand ToggleMenuCommand { get; }
        public IRelayCommand<ModuleItem> ChangeModuleCommand { get; }
        public IRelayCommand OpenYoutubeCommand { get; }
        public IRelayCommand OpenCivitaiCommand { get; }
        public IRelayCommand OpenSettingsCommand { get; }
        public IRelayCommand OpenPatreonCommand { get; }
        public IRelayCommand OpenAboutCommand { get; }

        public LogViewModel LogViewModel { get; } = new LogViewModel();

        private bool _isMenuOpen = true;
        public bool IsMenuOpen
        {
            get => _isMenuOpen;
            set => SetProperty(ref _isMenuOpen, value);
        }

        private object _currentModuleView = null!;
        public object CurrentModuleView
        {
            get => _currentModuleView;
            set => SetProperty(ref _currentModuleView, value);
        }

        public MainWindowViewModel()
        {
            ToggleMenuCommand = new RelayCommand(() =>
            {
                IsMenuOpen = !IsMenuOpen;
            });

            ChangeModuleCommand = new RelayCommand<ModuleItem>(mod =>
            {
                CurrentModuleView = mod!.View;
            });

            OpenYoutubeCommand = new RelayCommand(() =>
            {
                OpenUrl("https://youtube.com/@AIKnowledge2Go");
            });

            OpenCivitaiCommand = new RelayCommand(() =>
            {
                OpenUrl("https://civitai.com/user/AIknowlege2go");
            });

            OpenPatreonCommand = new RelayCommand(() =>
            {
                OpenUrl("https://patreon.com/AIKnowledgeCentral?utm_medium=unknown&utm_source=join_link&utm_campaign=creatorshare_creator&utm_content=copyLink");
            });

            OpenSettingsCommand = new RelayCommand(() =>
            {
                CurrentModuleView = new SettingsView();
            });

            OpenAboutCommand = new RelayCommand(() =>
            {
                CurrentModuleView = new AboutView();
            });

            CurrentModuleView = Modules.First().View;
        }

        private void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}