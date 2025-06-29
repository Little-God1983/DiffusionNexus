using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views;

public partial class LoraHelperView : UserControl
{
    public LoraHelperView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnSuggestionChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ViewModels.LoraHelperViewModel vm && sender is ComboBox cb && cb.SelectedItem is string text)
        {
            vm.ApplySuggestion(text);
            cb.IsDropDownOpen = false;
            cb.SelectedIndex = -1;
        }
    }
}
