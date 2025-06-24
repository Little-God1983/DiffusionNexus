using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Controls
{
    public partial class LoraSortCustomMappingsControl : UserControl
    {
        public LoraSortCustomMappingsControl()
        {
            InitializeComponent();
            DataContext = new LoraSortCustomMappingsViewModel();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
