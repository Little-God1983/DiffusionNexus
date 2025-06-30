using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.Classes;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views
{
    public partial class LoraSortView : UserControl
    {
        public LoraSortView()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}