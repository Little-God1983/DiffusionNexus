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
            // The coordination between view models is now handled in LoraSortViewModel
            // so we don't need the manual property synchronization here anymore
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
