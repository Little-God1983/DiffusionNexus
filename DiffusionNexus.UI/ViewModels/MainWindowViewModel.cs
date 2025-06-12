using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using ReactiveUI;
using DiffusionNexus.UI.Models;
using DiffusionNexus.UI.Views;

namespace DiffusionNexus.UI.ViewModels
{
    public class MainWindowViewModel : ReactiveObject
    {
        public ObservableCollection<ModuleItem> Modules { get; } = new()
        {
            new ModuleItem("Lora Sort", "/Assets/sort.png", new LoraSortView()),
            new ModuleItem("Prompt Edit", "/Assets/edit.png", new PromptEditView()),
            new ModuleItem("Lora Helper", "/Assets/help.png", new LoraHelperView())
        };

        public ReactiveCommand<Unit, Unit> ToggleMenuCommand { get; }
        public ReactiveCommand<ModuleItem, Unit> ChangeModuleCommand { get; }
        public ReactiveCommand<Unit, Unit> OpenYoutubeCommand { get; }
        public ReactiveCommand<Unit, Unit> OpenCivitaiCommand { get; }
        public ReactiveCommand<Unit, Unit> OpenSettingsCommand { get; }

        private bool _isMenuOpen = true;
        public bool IsMenuOpen
        {
            get => _isMenuOpen;
            set => this.RaiseAndSetIfChanged(ref _isMenuOpen, value);
        }

        private object _currentModuleView;
        public object CurrentModuleView
        {
            get => _currentModuleView;
            set => this.RaiseAndSetIfChanged(ref _currentModuleView, value);
        }

        public MainWindowViewModel()
        {
            ToggleMenuCommand = ReactiveCommand.Create(() => IsMenuOpen = !IsMenuOpen);
            ChangeModuleCommand = ReactiveCommand.Create<ModuleItem>(mod => CurrentModuleView = mod.View);
            OpenYoutubeCommand = ReactiveCommand.Create(() => OpenUrl("https://youtube.com/@AIKnowledge2Go"));
            OpenCivitaiCommand = ReactiveCommand.Create(() => OpenUrl("https://civitai.com/"));
            OpenSettingsCommand = ReactiveCommand.Create(() => CurrentModuleView = new SettingsView());

            CurrentModuleView = Modules.First().View;
        }

        private void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}