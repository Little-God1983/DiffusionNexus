using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using ReactiveUI;
using System.Reactive;
using System.Reactive.Linq;
using DiffusionNexus.UI.Views;
using DiffusionNexus.UI.Classes;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.ViewModels
{
    public class MainWindowViewModel : ReactiveObject
    {
        public ObservableCollection<ModuleItem> Modules { get; } = new ObservableCollection<ModuleItem>
        {
            new("Lora Sort", "avares://DiffusionNexus.UI/Assets/LoraSort.png", new LoraSortView()),
            new("Prompt Edit", "avares://DiffusionNexus.UI/Assets/PromptEdit.png", new PromptEditView()),
            new("Lora Helper", "avares://DiffusionNexus.UI/Assets/HumanCogwheel.png", new LoraHelperView())
        };

        public ReactiveCommand<Unit, Unit> ToggleMenuCommand { get; private set; }
        public ReactiveCommand<ModuleItem, Unit> ChangeModuleCommand { get; private set; }
        public ReactiveCommand<Unit, Unit> OpenYoutubeCommand { get; private set; }
        public ReactiveCommand<Unit, Unit> OpenCivitaiCommand { get; private set; }
        public ReactiveCommand<Unit, Unit> OpenSettingsCommand { get; private set; }
        public ReactiveCommand<Unit, Unit> OpenPatreonCommand { get; private set; }
        public ReactiveCommand<Unit, Unit> OpenAboutCommand { get; private set; }

        public LogViewModel LogViewModel { get; } = new LogViewModel();

        private bool _isMenuOpen = true;
        public bool IsMenuOpen
        {
            get => _isMenuOpen;
            set => this.RaiseAndSetIfChanged(ref _isMenuOpen, value);
        }

        private object _currentModuleView = null!;
        public object CurrentModuleView
        {
            get => _currentModuleView;
            set => this.RaiseAndSetIfChanged(ref _currentModuleView, value);
        }

        public MainWindowViewModel()
        {
            ToggleMenuCommand = ReactiveCommand.Create(() =>
            {
                IsMenuOpen = !IsMenuOpen;
            });

            ChangeModuleCommand = ReactiveCommand.Create<ModuleItem>(mod =>
            {
                CurrentModuleView = mod.View;
            });

            OpenYoutubeCommand = ReactiveCommand.Create(() =>
            {
                OpenUrl("https://youtube.com/@AIKnowledge2Go");
            });

            OpenCivitaiCommand = ReactiveCommand.Create(() =>
            {
                OpenUrl("https://civitai.com/user/AIknowlege2go");
            });

            OpenPatreonCommand = ReactiveCommand.Create(() =>
            {
                OpenUrl("https://patreon.com/AIKnowledgeCentral?utm_medium=unknown&utm_source=join_link&utm_campaign=creatorshare_creator&utm_content=copyLink");
            });

            OpenSettingsCommand = ReactiveCommand.Create(() =>
            {
                CurrentModuleView = new SettingsView();
            });

            OpenAboutCommand = ReactiveCommand.Create(() =>
            {
                CurrentModuleView = new SettingsView();
            });

            CurrentModuleView = Modules.First().View;
        }

        private void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}