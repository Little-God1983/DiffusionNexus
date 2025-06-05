using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffusionNexus.UI.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        public ObservableCollection<Module> Modules { get; }

        [ObservableProperty]
        private Module? currentModule;

        public IRelayCommand<Module> SwitchModuleCommand { get; }
        public IRelayCommand OpenOptionsCommand { get; }
        public IRelayCommand OpenPatreonCommand { get; }
        public IRelayCommand OpenYouTubeCommand { get; }

        public MainWindowViewModel()
        {
            Modules = new ObservableCollection<Module>
            {
                new Module("Module 1", new Views.Module1View()),
                new Module("Module 2", new Views.Module2View()),
                new Module("Module 3", new Views.Module3View())
            };

            CurrentModule = Modules[0];

            SwitchModuleCommand = new RelayCommand<Module>(module => CurrentModule = module);
            OpenOptionsCommand = new RelayCommand(() => { /* options placeholder */ });
            OpenPatreonCommand = new RelayCommand(() => OpenLink("https://patreon.com"));
            OpenYouTubeCommand = new RelayCommand(() => OpenLink("https://youtube.com"));
        }

        private void OpenLink(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
                // ignore
            }
        }
    }
}
