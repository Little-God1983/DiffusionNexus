using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia;
using Avalonia.VisualTree;
using DiffusionNexus.UI.ViewModels;
using System.ComponentModel;

namespace DiffusionNexus.UI.Views
{
    public partial class LoraSortView : UserControl
    {
        private LoraSortMainSettingsViewModel? _mainVm;
        private LoraSortCustomMappingsViewModel? _mapVm;

        public LoraSortView()
        {
            InitializeComponent();
            this.AttachedToVisualTree += OnAttached;
        }

        private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            HookViewModels();
            if (MainSettingsControl is not null)
                MainSettingsControl.DataContextChanged += (_, _) => HookViewModels();
            if (CustomMappingsControl is not null)
                CustomMappingsControl.DataContextChanged += (_, _) => HookViewModels();
        }

        private void HookViewModels()
        {
            if (_mainVm is not null)
                _mainVm.PropertyChanged -= MainVmOnPropertyChanged;

            _mainVm = MainSettingsControl?.DataContext as LoraSortMainSettingsViewModel;
            _mapVm = CustomMappingsControl?.DataContext as LoraSortCustomMappingsViewModel;

            if (_mainVm is not null && _mapVm is not null)
            {
                _mapVm.IsCustomEnabled = _mainVm.UseCustomMappings;
                _mainVm.PropertyChanged += MainVmOnPropertyChanged;
            }
        }

        private void MainVmOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LoraSortMainSettingsViewModel.UseCustomMappings))
            {
                if (_mainVm is not null && _mapVm is not null)
                    _mapVm.IsCustomEnabled = _mainVm.UseCustomMappings;
            }
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}