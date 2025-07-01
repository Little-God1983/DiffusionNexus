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
        public LoraSortView()
        {
            InitializeComponent();
            this.AttachedToVisualTree += OnAttached;
        }

        private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            if (MainSettingsControl?.DataContext is LoraSortMainSettingsViewModel mainVm &&
                CustomMappingsControl?.DataContext is LoraSortCustomMappingsViewModel mapVm)
            {
                mapVm.IsCustomEnabled = mainVm.UseCustomMappings;
                mainVm.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(LoraSortMainSettingsViewModel.UseCustomMappings))
                    {
                        mapVm.IsCustomEnabled = mainVm.UseCustomMappings;
                    }
                };
            }
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}