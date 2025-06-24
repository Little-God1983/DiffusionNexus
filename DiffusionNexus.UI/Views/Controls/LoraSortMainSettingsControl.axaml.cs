using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Controls
{
    public partial class LoraSortMainSettingsControl : UserControl
    {
        public LoraSortMainSettingsControl()
        {
            InitializeComponent();
            DataContext = new LoraSortMainSettingsViewModel();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}