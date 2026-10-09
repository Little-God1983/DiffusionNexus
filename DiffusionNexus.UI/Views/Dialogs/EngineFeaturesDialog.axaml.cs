using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>The Engine tile's Features dialog. All logic lives in <see cref="ViewModels.EngineFeaturesViewModel"/>.</summary>
public partial class EngineFeaturesDialog : Window
{
    public EngineFeaturesDialog()
    {
        AvaloniaXamlLoader.Load(this);
        // Closing mid-install would orphan the download; the Close button is disabled meanwhile,
        // and the window's own close box is refused the same way.
        Closing += (_, e) =>
        {
            if (DataContext is ViewModels.EngineFeaturesViewModel { IsInstalling: true })
                e.Cancel = true;
        };
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
