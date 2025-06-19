using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using ReactiveUI;
using System.Reactive;
using System.Reactive.Linq;
using DiffusionNexus.UI.Views;
using DiffusionNexus.UI.Classes;

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

        private bool _isMenuOpen = true;
        public bool IsMenuOpen
        {
            get => _isMenuOpen;
            set
            {
                this.RaiseAndSetIfChanged(ref _isMenuOpen, value);
                UpdateLayoutFromMenuState();
            }
        }

        private double _sidebarWidth = 200;
        public double SidebarWidth
        {
            get => _sidebarWidth;
            set => this.RaiseAndSetIfChanged(ref _sidebarWidth, value);
        }

        private double _mainContentScale = 1.0;
        public double MainContentScale
        {
            get => _mainContentScale;
            set => this.RaiseAndSetIfChanged(ref _mainContentScale, value);
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
                OpenUrl("https://civitai.com/");
            });

            OpenSettingsCommand = ReactiveCommand.Create(() =>
            {
                CurrentModuleView = new SettingsView();
            });

            CurrentModuleView = Modules.First().View;
            UpdateLayoutFromMenuState();
        }

        private void UpdateLayoutFromMenuState()
        {
            SidebarWidth = IsMenuOpen ? 200 : 0;
            MainContentScale = IsMenuOpen ? 0.9 : 1.0;
        }

        private void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}