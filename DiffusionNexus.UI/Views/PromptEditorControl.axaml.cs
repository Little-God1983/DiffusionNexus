using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views;

public partial class PromptEditorControl : UserControl
{

    public PromptEditorControl()
    {
        InitializeComponent();
        DataContext = new PromptEditorControlViewModel();
    }
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}