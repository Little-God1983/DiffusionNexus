using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using ReactiveUI;
using System.Reactive;
using System.Reactive.Linq;
using DiffusionNexus.UI.Classes;
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

        public ReactiveCommand<Unit, Unit> ToggleMenuCommand { get; private set; }
        public ReactiveCommand<ModuleItem, Unit> ChangeModuleCommand { get; private set; }
        public ReactiveCommand<Unit, Unit> OpenYoutubeCommand { get; private set; }
        public ReactiveCommand<Unit, Unit> OpenCivitaiCommand { get; private set; }
        public ReactiveCommand<Unit, Unit> OpenSettingsCommand { get; private set; }

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
            ToggleMenuCommand = ReactiveCommand.CreateFromObservable(() =>
            {
                IsMenuOpen = !IsMenuOpen;
                return Observable.Return(Unit.Default);
            });

            ChangeModuleCommand = ReactiveCommand.CreateFromObservable<ModuleItem, Unit>(mod =>
            {
                CurrentModuleView = mod.View;
                return Observable.Return(Unit.Default);
            });

            OpenYoutubeCommand = ReactiveCommand.CreateFromObservable(() =>
            {
                OpenUrl("https://youtube.com/@AIKnowledge2Go");
                return Observable.Return(Unit.Default);
            });

            OpenCivitaiCommand = ReactiveCommand.CreateFromObservable(() =>
            {
                OpenUrl("https://civitai.com/");
                return Observable.Return(Unit.Default);
            });

            OpenSettingsCommand = ReactiveCommand.CreateFromObservable(() =>
            {
                CurrentModuleView = new SettingsView();
                return Observable.Return(Unit.Default);
            });

            CurrentModuleView = Modules.First().View;
        }

        private void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}